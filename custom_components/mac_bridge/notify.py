"""notify.<computer name> for Mac Bridge, with optional action buttons."""

from __future__ import annotations

from typing import Any

import voluptuous as vol

from homeassistant.components.notify import ATTR_DATA, ATTR_TITLE, BaseNotificationService
from homeassistant.config_entries import ConfigEntryState
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ServiceValidationError
from homeassistant.helpers.typing import ConfigType, DiscoveryInfoType

from . import NOTIFY_ACTIONS


async def async_get_service(
    hass: HomeAssistant, config: ConfigType, discovery_info: DiscoveryInfoType | None = None
) -> BaseNotificationService | None:
    if discovery_info is None:
        return None
    return MacBridgeNotificationService(discovery_info["entry_id"])


class MacBridgeNotificationService(BaseNotificationService):
    """Sends a notification to the computer. Clicked buttons fire mac_bridge_notification_action."""

    def __init__(self, entry_id: str) -> None:
        self._entry_id = entry_id

    async def async_send_message(self, message: str = "", **kwargs: Any) -> None:
        entry = self.hass.config_entries.async_get_entry(self._entry_id)
        if entry is None or entry.state is not ConfigEntryState.LOADED:
            raise ServiceValidationError("The computer is not set up in Mac Bridge")
        value: dict[str, Any] = {"message": str(message)[:500], "title": str(kwargs.get(ATTR_TITLE) or "Home Assistant")[:100]}
        data = kwargs.get(ATTR_DATA) or {}
        if actions := data.get("actions"):
            try:
                value["data"] = {"actions": NOTIFY_ACTIONS(actions)}
            except vol.Invalid as err:
                raise ServiceValidationError(f"Invalid actions: {err}") from err
        await entry.runtime_data.async_command("notify", value)
