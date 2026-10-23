"""Data coordinator for Mac Bridge."""

from __future__ import annotations

from datetime import timedelta
import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed, HomeAssistantError
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import MacBridgeAuthError, MacBridgeClient, MacBridgeError
from .const import DOMAIN

_LOGGER = logging.getLogger(__name__)


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
            return await self.client.status()
        except MacBridgeAuthError as err:
            raise ConfigEntryAuthFailed from err
        except MacBridgeError as err:
            # Mac is asleep / off: report it instead of failing so entities show "off".
            if self.data:
                return {**self.data, "online": False}
            raise UpdateFailed(str(err)) from err

    async def async_command(self, command: str, value: Any = None) -> None:
        try:
            result = await self.client.command(command, value)
        except MacBridgeError as err:
            raise HomeAssistantError(f"Command {command} failed: {err}") from err
        if status := result.get("status"):
            self.async_set_updated_data(status)
