"""Binary sensors for Mac Bridge."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from homeassistant.components.binary_sensor import (
    BinarySensorDeviceClass,
    BinarySensorEntity,
    BinarySensorEntityDescription,
)
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
    sensors = coordinator.data.get("sensors", {})
    entities.extend(MacBinarySensor(coordinator, d) for d in BINARY_SENSORS if d.key in sensors)
    # Apps/programs chosen in the MacBridge menu.
    entities.extend(MacProcessSensor(coordinator, str(name)) for name in (sensors.get("processes") or {}))
    async_add_entities(entities)


@dataclass(frozen=True, kw_only=True)
class MacBinarySensorDescription(BinarySensorEntityDescription):
    value_fn: Callable[[dict[str, Any]], bool | None]


def _flag(key: str) -> Callable[[dict[str, Any]], bool | None]:
    def value(data: dict[str, Any]) -> bool | None:
        v = data.get("sensors", {}).get(key)
        return v if isinstance(v, bool) else None

    return value


BINARY_SENSORS: tuple[MacBinarySensorDescription, ...] = (
    MacBinarySensorDescription(key="session_locked", translation_key="session_locked", icon="mdi:lock",
                               value_fn=_flag("session_locked")),
    MacBinarySensorDescription(key="display_on", translation_key="display_on", icon="mdi:monitor",
                               value_fn=_flag("display_on")),
    MacBinarySensorDescription(key="microphone_active", translation_key="microphone_active", icon="mdi:microphone",
                               value_fn=_flag("microphone_active")),
    MacBinarySensorDescription(key="webcam_active", translation_key="webcam_active", icon="mdi:webcam",
                               value_fn=_flag("webcam_active")),
)


class MacBinarySensor(MacBridgeEntity, BinarySensorEntity):
    entity_description: MacBinarySensorDescription

    def __init__(self, coordinator, description: MacBinarySensorDescription) -> None:
        super().__init__(coordinator, description.key)
        self.entity_description = description

    @property
    def available(self) -> bool:
        return super().available and self.online

    @property
    def is_on(self) -> bool | None:
        return self.entity_description.value_fn(self.coordinator.data)


class MacProcessSensor(MacBridgeEntity, BinarySensorEntity):
    """Whether an app/program chosen in the MacBridge menu is running."""

    _attr_icon = "mdi:application-cog"
    _attr_device_class = BinarySensorDeviceClass.RUNNING
    _attr_translation_key = "process"

    def __init__(self, coordinator, name: str) -> None:
        super().__init__(coordinator, f"process_{name.lower()}")
        self._process = name
        self._attr_translation_placeholders = {"name": name}

    @property
    def available(self) -> bool:
        return super().available and self.online and self._process in (self.coordinator.data.get("sensors", {}).get("processes") or {})

    @property
    def is_on(self) -> bool | None:
        return (self.coordinator.data.get("sensors", {}).get("processes") or {}).get(self._process)


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
