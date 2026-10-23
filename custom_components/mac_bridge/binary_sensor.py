"""Binary sensors for Mac Bridge."""

from __future__ import annotations

from homeassistant.components.binary_sensor import BinarySensorDeviceClass, BinarySensorEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import MacBridgeConfigEntry
from .entity import MacBridgeEntity


async def async_setup_entry(
    hass: HomeAssistant, entry: MacBridgeConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    coordinator = entry.runtime_data
    entities: list[BinarySensorEntity] = [MacOnline(coordinator)]
    if "battery_charging" in coordinator.data.get("stats", {}):
        entities.append(MacCharging(coordinator))
    async_add_entities(entities)


class MacOnline(MacBridgeEntity, BinarySensorEntity):
    _attr_translation_key = "online"
    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY

    def __init__(self, coordinator) -> None:
        super().__init__(coordinator, "online")

    @property
    def available(self) -> bool:
        return True

    @property
    def is_on(self) -> bool:
        return self.coordinator.last_update_success and self.online


class MacCharging(MacBridgeEntity, BinarySensorEntity):
    _attr_translation_key = "charging"
    _attr_device_class = BinarySensorDeviceClass.BATTERY_CHARGING

    def __init__(self, coordinator) -> None:
        super().__init__(coordinator, "charging")

    @property
    def available(self) -> bool:
        return super().available and self.online

    @property
    def is_on(self) -> bool:
        return bool(self.stats.get("battery_charging"))
