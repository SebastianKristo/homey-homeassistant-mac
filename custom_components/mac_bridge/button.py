"""Command buttons for Mac Bridge."""

from __future__ import annotations

from homeassistant.components.button import ButtonDeviceClass, ButtonEntity, ButtonEntityDescription
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import MacBridgeConfigEntry
from .entity import MacBridgeEntity

BUTTONS: tuple[ButtonEntityDescription, ...] = (
    ButtonEntityDescription(key="wake", translation_key="wake", icon="mdi:power-on"),
    ButtonEntityDescription(key="sleep", translation_key="sleep", icon="mdi:sleep"),
    ButtonEntityDescription(key="display_sleep", translation_key="display_sleep", icon="mdi:monitor-off"),
    ButtonEntityDescription(key="wake_display", translation_key="wake_display", icon="mdi:monitor"),
    ButtonEntityDescription(key="lock", translation_key="lock", icon="mdi:lock"),
    ButtonEntityDescription(key="restart", translation_key="restart", device_class=ButtonDeviceClass.RESTART),
    ButtonEntityDescription(key="shutdown", translation_key="shutdown", icon="mdi:power"),
    ButtonEntityDescription(key="play_pause", translation_key="play_pause", icon="mdi:play-pause"),
    ButtonEntityDescription(key="next", translation_key="next", icon="mdi:skip-next"),
    ButtonEntityDescription(key="previous", translation_key="previous", icon="mdi:skip-previous"),
    ButtonEntityDescription(key="mute", translation_key="mute", icon="mdi:volume-off"),
)


async def async_setup_entry(
    hass: HomeAssistant, entry: MacBridgeConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    async_add_entities(MacButton(entry.runtime_data, desc) for desc in BUTTONS)


class MacButton(MacBridgeEntity, ButtonEntity):
    def __init__(self, coordinator, description: ButtonEntityDescription) -> None:
        super().__init__(coordinator, f"button_{description.key}")
        self.entity_description = description

    @property
    def available(self) -> bool:
        if self.entity_description.key == "wake":
            # Wake-on-LAN works while the Mac sleeps.
            return self.online or self.coordinator.mac_address is not None
        if self.entity_description.key in ("restart", "shutdown") and not self.coordinator.data.get(
            "power_commands", True
        ):
            return False  # turned off in the MacBridge menu
        return super().available and self.online

    async def async_press(self) -> None:
        if self.entity_description.key == "wake":
            await self.coordinator.async_wake()
        else:
            await self.coordinator.async_command(self.entity_description.key)
