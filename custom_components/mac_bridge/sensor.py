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
from homeassistant.const import PERCENTAGE, EntityCategory, UnitOfFrequency, UnitOfInformation, UnitOfTime
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.util import dt as dt_util

from . import MacBridgeConfigEntry
from .entity import MacBridgeEntity


@dataclass(frozen=True, kw_only=True)
class MacSensorDescription(SensorEntityDescription):
    value_fn: Callable[[dict[str, Any]], Any] | None = None
    attrs_fn: Callable[[dict[str, Any]], dict[str, Any]] | None = None
    # Key in the computer's "sensors" (or "stats") that must exist for the entity to be created.
    requires: str | None = None


def _stat(key: str) -> Callable[[dict[str, Any]], Any]:
    return lambda data: data.get("stats", {}).get(key)


def _sensor(key: str) -> Callable[[dict[str, Any]], Any]:
    return lambda data: data.get("sensors", {}).get(key)


def _count(key: str) -> Callable[[dict[str, Any]], Any]:
    def value(data: dict[str, Any]) -> int | None:
        items = data.get("sensors", {}).get(key)
        return len(items) if isinstance(items, list) else None

    return value


def _names(key: str) -> Callable[[dict[str, Any]], dict[str, Any]]:
    def attrs(data: dict[str, Any]) -> dict[str, Any]:
        items = data.get("sensors", {}).get(key)
        return {"names": [str(i) for i in items][:50]} if isinstance(items, list) else {}

    return attrs


def _joined(key: str) -> Callable[[dict[str, Any]], Any]:
    def value(data: dict[str, Any]) -> str | None:
        items = data.get("sensors", {}).get(key)
        return ", ".join(str(i) for i in items)[:255] if isinstance(items, list) else None

    return value


def _epoch(key: str) -> Callable[[dict[str, Any]], datetime | None]:
    def value(data: dict[str, Any]) -> datetime | None:
        seconds = data.get("sensors", {}).get(key)
        return dt_util.utc_from_timestamp(seconds) if isinstance(seconds, (int, float)) else None

    return value


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
    # Sensors from the "sensors" section (MacBridge 1.3+). Each is created only if reported.
    MacSensorDescription(key="active_app", translation_key="active_app", icon="mdi:application",
                         value_fn=_sensor("active_app"), requires="active_app"),
    MacSensorDescription(key="last_active", translation_key="last_active", device_class=SensorDeviceClass.TIMESTAMP,
                         value_fn=_epoch("last_active"), requires="last_active"),
    MacSensorDescription(key="idle_time", translation_key="idle_time", native_unit_of_measurement=UnitOfTime.SECONDS,
                         device_class=SensorDeviceClass.DURATION, state_class=SensorStateClass.MEASUREMENT,
                         suggested_unit_of_measurement=UnitOfTime.MINUTES, value_fn=_sensor("idle_seconds"),
                         requires="idle_seconds"),
    MacSensorDescription(key="logged_user", translation_key="logged_user", icon="mdi:account",
                         value_fn=_sensor("logged_user"), requires="logged_user"),
    MacSensorDescription(key="logged_users", translation_key="logged_users", icon="mdi:account-multiple",
                         value_fn=_count("logged_users"), attrs_fn=_names("logged_users"), requires="logged_users"),
    MacSensorDescription(key="gpu_usage", translation_key="gpu_usage", native_unit_of_measurement=PERCENTAGE,
                         state_class=SensorStateClass.MEASUREMENT, icon="mdi:expansion-card",
                         value_fn=_sensor("gpu_usage"), requires="gpu_usage"),
    MacSensorDescription(key="cpu_clock", translation_key="cpu_clock", native_unit_of_measurement=UnitOfFrequency.MEGAHERTZ,
                         device_class=SensorDeviceClass.FREQUENCY, state_class=SensorStateClass.MEASUREMENT,
                         value_fn=_sensor("cpu_clock_mhz"), requires="cpu_clock_mhz"),
    MacSensorDescription(key="microphone_apps", translation_key="microphone_apps", icon="mdi:microphone",
                         value_fn=_joined("microphone_apps"), requires="microphone_apps"),
    MacSensorDescription(key="webcam_apps", translation_key="webcam_apps", icon="mdi:webcam",
                         value_fn=_joined("webcam_apps"), requires="webcam_apps"),
    MacSensorDescription(key="audio_output", translation_key="audio_output", icon="mdi:speaker",
                         value_fn=_sensor("audio_output"), requires="audio_output"),
    MacSensorDescription(key="audio_input", translation_key="audio_input", icon="mdi:microphone-outline",
                         value_fn=_sensor("audio_input"), requires="audio_input"),
    MacSensorDescription(key="displays", translation_key="displays", icon="mdi:monitor-multiple",
                         value_fn=_sensor("displays"),
                         attrs_fn=lambda d: {"resolution": d.get("sensors", {}).get("display_resolution")},
                         requires="displays"),
    MacSensorDescription(key="accent_color", translation_key="accent_color", icon="mdi:palette",
                         entity_category=EntityCategory.DIAGNOSTIC, value_fn=_sensor("accent_color"),
                         requires="accent_color"),
    MacSensorDescription(key="printers", translation_key="printers", icon="mdi:printer",
                         entity_category=EntityCategory.DIAGNOSTIC, value_fn=_count("printers"),
                         attrs_fn=_names("printers"), requires="printers"),
    MacSensorDescription(key="ip_address", translation_key="ip_address", icon="mdi:ip-network",
                         entity_category=EntityCategory.DIAGNOSTIC, value_fn=_sensor("ip_address"),
                         attrs_fn=lambda d: {"interface": d.get("sensors", {}).get("network_interface")},
                         requires="ip_address"),
    MacSensorDescription(key="bluetooth_devices", translation_key="bluetooth_devices", icon="mdi:bluetooth-connect",
                         value_fn=_count("bluetooth_devices"), attrs_fn=_names("bluetooth_devices"),
                         requires="bluetooth_devices"),
    MacSensorDescription(key="bluetooth_le_devices", translation_key="bluetooth_le_devices", icon="mdi:bluetooth-connect",
                         value_fn=_count("bluetooth_le_devices"), attrs_fn=_names("bluetooth_le_devices"),
                         requires="bluetooth_le_devices"),
    MacSensorDescription(key="notification_state", translation_key="notification_state", device_class=SensorDeviceClass.ENUM,
                         options=["not_present", "busy", "fullscreen_game", "presentation", "accepts_notifications",
                                  "quiet_time", "fullscreen_app", "unknown"],
                         icon="mdi:bell-cog", value_fn=_sensor("notification_state"), requires="notification_state"),
    MacSensorDescription(key="windows_updates", translation_key="windows_updates", icon="mdi:microsoft-windows",
                         state_class=SensorStateClass.MEASUREMENT, value_fn=_sensor("windows_updates"),
                         requires="windows_updates"),
    MacSensorDescription(key="last_state_change", translation_key="last_state_change", icon="mdi:power-settings",
                         value_fn=_sensor("last_state_change"),
                         attrs_fn=lambda d: {"changed_at": d.get("sensors", {}).get("last_state_change_at")},
                         requires="last_state_change"),
)


async def async_setup_entry(
    hass: HomeAssistant, entry: MacBridgeConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    coordinator = entry.runtime_data
    stats = coordinator.data.get("stats", {})
    # Only created when the computer reports them (no battery on desktops; Windows has no load
    # average or thermal state).
    optional = {"battery": "battery_level", "load_1m": "load_1m", "thermal_state": "thermal_state"}
    sensors = coordinator.data.get("sensors", {})
    entities: list[SensorEntity] = [
        MacSensor(coordinator, desc)
        for desc in SENSORS
        if (desc.key not in optional or optional[desc.key] in stats)
        and (desc.requires is None or desc.requires in sensors)
    ]
    # Windows services chosen in the MacBridge tray menu.
    for name in (sensors.get("services") or {}):
        entities.append(MacServiceSensor(coordinator, str(name)))
    async_add_entities(entities)


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

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        if self.entity_description.attrs_fn is None:
            return None
        return self.entity_description.attrs_fn(self.coordinator.data)


class MacServiceSensor(MacBridgeEntity, SensorEntity):
    """State of a Windows service (running, stopped, …)."""

    _attr_icon = "mdi:cog"
    _attr_translation_key = "service"

    def __init__(self, coordinator, name: str) -> None:
        super().__init__(coordinator, f"service_{name.lower()}")
        self._service = name
        self._attr_translation_placeholders = {"name": name}

    @property
    def available(self) -> bool:
        return super().available and self.online and self._service in (self.coordinator.data.get("sensors", {}).get("services") or {})

    @property
    def native_value(self) -> str | None:
        return (self.coordinator.data.get("sensors", {}).get("services") or {}).get(self._service)
