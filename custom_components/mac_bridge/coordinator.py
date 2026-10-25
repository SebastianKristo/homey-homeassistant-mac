"""Data coordinator for Mac Bridge."""

from __future__ import annotations

from datetime import timedelta
import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_HOST
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import ConfigEntryAuthFailed, HomeAssistantError
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.event import async_call_later
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import MacBridgeAuthError, MacBridgeClient, MacBridgeError, async_wake_on_lan
from .const import CONF_DEVICE, DOMAIN, EVENT_NOTIFICATION_ACTION, EVENT_STATE_CHANGE

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
        # Highest event id already handled; None until the first status (older events are not replayed).
        self._last_event_id: int | None = None

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
        self.ingest(data)
        return data

    @callback
    def ingest(self, data: dict[str, Any]) -> None:
        """Handle a fresh status, however it arrived (poll, push or command reply)."""
        self.remember_device(data)
        self._fire_new_events(data.get("events"))

    @callback
    def _fire_new_events(self, events: Any) -> None:
        """Turn new events from the computer (notification buttons, sleep/wake, …) into bus events."""
        if not isinstance(events, list):
            return
        valid = [e for e in events if isinstance(e, dict) and isinstance(e.get("id"), int)]
        newest = max((e["id"] for e in valid), default=None)
        if self._last_event_id is None:
            self._last_event_id = newest if newest is not None else 0
            return
        device = dr.async_get(self.hass).async_get_device(identifiers={(DOMAIN, self.config_entry.unique_id)})
        base = {"device_id": device.id if device else None, "name": self.config_entry.title}
        for event in sorted(valid, key=lambda e: e["id"]):
            if event["id"] <= self._last_event_id:
                continue
            self._last_event_id = event["id"]
            if event.get("type") == "notification_action" and isinstance(event.get("action"), str):
                self.hass.bus.async_fire(
                    EVENT_NOTIFICATION_ACTION,
                    {**base, "action": event["action"][:64], "title": str(event.get("title", ""))[:100]},
                )
            elif event.get("type") == "state_change" and isinstance(event.get("state"), str):
                self.hass.bus.async_fire(EVENT_STATE_CHANGE, {**base, "state": event["state"][:32]})

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
            self.ingest(status)
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
