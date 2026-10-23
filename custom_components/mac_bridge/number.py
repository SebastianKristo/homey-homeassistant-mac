"""Volume slider for Mac Bridge."""

from __future__ import annotations

from homeassistant.components.number import NumberEntity, NumberMode
from homeassistant.const import PERCENTAGE
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import MacBridgeConfigEntry
from .entity import MacBridgeEntity


async def async_setup_entry(
    hass: HomeAssistant, entry: MacBridgeConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    async_add_entities([MacVolume(entry.runtime_data)])


class MacVolume(MacBridgeEntity, NumberEntity):
    _attr_translation_key = "volume"
    _attr_icon = "mdi:volume-high"
    _attr_native_min_value = 0
    _attr_native_max_value = 100
    _attr_native_step = 1
    _attr_native_unit_of_measurement = PERCENTAGE
    _attr_mode = NumberMode.SLIDER

    def __init__(self, coordinator) -> None:
        super().__init__(coordinator, "volume_number")

    @property
    def available(self) -> bool:
        return super().available and self.online

    @property
    def native_value(self) -> float | None:
        return self.coordinator.data.get("volume")

    async def async_set_native_value(self, value: float) -> None:
        await self.coordinator.async_command("set_volume", int(value))
