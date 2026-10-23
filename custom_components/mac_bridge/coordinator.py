"""Data coordinator for Mac Bridge."""

from __future__ import annotations

from datetime import timedelta
import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_HOST
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import ConfigEntryAuthFailed, HomeAssistantError
from homeassistant.helpers.event import async_call_later
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import MacBridgeAuthError, MacBridgeClient, MacBridgeError, async_wake_on_lan
from .const import CONF_DEVICE, DOMAIN

_LOGGER = logging.getLogger(__name__)

REMEMBERED_DEVICE_KEYS = ("id", "name", "model", "manufacturer", "platform", "os_name", "os_version", "mac_address")


class MacBridgeCoordinator(DataUpdateCoordinator[dict[str, Any]]):
    """Polls the Mac and accepts pushed updates."""

    def __init__(
        self, hass: HomeAssistant, entry: ConfigEntry, client: MacBridgeClient, interval: int
    ) -> None:
        super().__init__(
            hass,
            _LOGGER,
            config_entry=entry,
            name=DOMAIN,
            update_interval=timedelta(seconds=interval),
        )
        self.client = client

    async def _async_update_data(self) -> dict[str, Any]:
        try:
            data = await self.client.status()
        except MacBridgeAuthError as err:
            raise ConfigEntryAuthFailed from err
        except MacBridgeError as err:
            # Mac is asleep / off: report it instead of failing so entities show "off".
            if self.data:
                return {**self.data, "online": False}
            if cached := self.config_entry.data.get(CONF_DEVICE):
                return {"device": cached, "online": False}
            raise UpdateFailed(str(err)) from err
        self.remember_device(data)
        return data

    @callback
    def remember_device(self, data: dict[str, Any]) -> None:
        """Store name/model/MAC address so the Mac can be shown and woken while it sleeps."""
        device = data.get("device")
        if not isinstance(device, dict):
            return
        remembered = {k: device[k] for k in REMEMBERED_DEVICE_KEYS if isinstance(device.get(k), str)}
        if remembered and remembered != self.config_entry.data.get(CONF_DEVICE):
            self.hass.config_entries.async_update_entry(
                self.config_entry, data={**self.config_entry.data, CONF_DEVICE: remembered}
            )

    @property
    def mac_address(self) -> str | None:
        device = (self.data or {}).get("device") or self.config_entry.data.get(CONF_DEVICE) or {}
        return device.get("mac_address")

    async def async_command(self, command: str, value: Any = None) -> None:
        try:
            result = await self.client.command(command, value)
        except MacBridgeError as err:
            raise HomeAssistantError(f"Command {command} failed: {err}") from err
        if status := result.get("status"):
            self.remember_device(status)
            self.async_set_updated_data(status)

    async def async_wake(self) -> None:
        """Wake-on-LAN for a sleeping Mac, and wake the display if it is already awake."""
        if mac := self.mac_address:
            await async_wake_on_lan(mac, self.config_entry.data[CONF_HOST])
        try:
            await self.async_command("wake")
        except HomeAssistantError:
            if not mac:
                raise
            # Still asleep: the magic packet does the work; next poll shows it online.
        async_call_later(self.hass, 10, self._async_refresh_later)

    async def _async_refresh_later(self, _now: Any) -> None:
        await self.async_request_refresh()
