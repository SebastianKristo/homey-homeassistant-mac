"""Performance sensors for Mac Bridge."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Any

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorEntityDescription,
    SensorStateClass,
)
from homeassistant.const import PERCENTAGE, EntityCategory, UnitOfInformation
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.util import dt as dt_util

from . import MacBridgeConfigEntry
from .entity import MacBridgeEntity


@dataclass(frozen=True, kw_only=True)
class MacSensorDescription(SensorEntityDescription):
    value_fn: Callable[[dict[str, Any]], Any] | None = None


def _stat(key: str) -> Callable[[dict[str, Any]], Any]:
    return lambda data: data.get("stats", {}).get(key)


def _boot_time(data: dict[str, Any]) -> datetime | None:
    uptime = data.get("stats", {}).get("uptime")
    if uptime is None:
        return None
    # Round to the minute so the state doesn't change on every poll.
    return (dt_util.utcnow() - timedelta(seconds=uptime)).replace(second=0, microsecond=0)


SENSORS: tuple[MacSensorDescription, ...] = (
    MacSensorDescription(key="cpu_usage", translation_key="cpu_usage", native_unit_of_measurement=PERCENTAGE,
                         state_class=SensorStateClass.MEASUREMENT, icon="mdi:cpu-64-bit", value_fn=_stat("cpu_usage")),
    MacSensorDescription(key="memory_usage", translation_key="memory_usage", native_unit_of_measurement=PERCENTAGE,
                         state_class=SensorStateClass.MEASUREMENT, icon="mdi:memory", value_fn=_stat("memory_usage")),
    MacSensorDescription(key="memory_used", translation_key="memory_used", native_unit_of_measurement=UnitOfInformation.GIGABYTES,
                         device_class=SensorDeviceClass.DATA_SIZE, state_class=SensorStateClass.MEASUREMENT,
                         value_fn=_stat("memory_used_gb")),
    MacSensorDescription(key="disk_usage", translation_key="disk_usage", native_unit_of_measurement=PERCENTAGE,
                         state_class=SensorStateClass.MEASUREMENT, icon="mdi:harddisk", value_fn=_stat("disk_usage")),
    MacSensorDescription(key="disk_free", translation_key="disk_free", native_unit_of_measurement=UnitOfInformation.GIGABYTES,
                         device_class=SensorDeviceClass.DATA_SIZE, state_class=SensorStateClass.MEASUREMENT,
                         value_fn=_stat("disk_free_gb")),
    MacSensorDescription(key="load_1m", translation_key="load_1m", state_class=SensorStateClass.MEASUREMENT,
                         icon="mdi:chart-line", value_fn=_stat("load_1m")),
    MacSensorDescription(key="battery", translation_key="battery", native_unit_of_measurement=PERCENTAGE,
                         device_class=SensorDeviceClass.BATTERY, state_class=SensorStateClass.MEASUREMENT,
                         value_fn=_stat("battery_level")),
    MacSensorDescription(key="thermal_state", translation_key="thermal_state", device_class=SensorDeviceClass.ENUM,
                         options=["nominal", "fair", "serious", "critical", "unknown"], icon="mdi:thermometer",
                         value_fn=_stat("thermal_state")),
    MacSensorDescription(key="last_boot", translation_key="last_boot", device_class=SensorDeviceClass.TIMESTAMP,
                         entity_category=EntityCategory.DIAGNOSTIC, value_fn=_boot_time),
    MacSensorDescription(key="volume", translation_key="volume", native_unit_of_measurement=PERCENTAGE,
                         icon="mdi:volume-high", value_fn=lambda d: d.get("volume")),
    MacSensorDescription(key="media_title", translation_key="media_title", icon="mdi:music",
                         value_fn=lambda d: d.get("media", {}).get("title") or None),
)


async def async_setup_entry(
    hass: HomeAssistant, entry: MacBridgeConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    coordinator = entry.runtime_data
    stats = coordinator.data.get("stats", {})
    async_add_entities(
        MacSensor(coordinator, desc)
        for desc in SENSORS
        if desc.key != "battery" or "battery_level" in stats
    )


class MacSensor(MacBridgeEntity, SensorEntity):
    entity_description: MacSensorDescription

    def __init__(self, coordinator, description: MacSensorDescription) -> None:
        super().__init__(coordinator, description.key)
        self.entity_description = description

    @property
    def available(self) -> bool:
        return super().available and self.online

    @property
    def native_value(self) -> Any:
        return self.entity_description.value_fn(self.coordinator.data)
