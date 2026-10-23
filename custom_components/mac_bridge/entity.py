"""Base entity for Mac Bridge."""

from __future__ import annotations

from typing import Any

from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .const import DOMAIN
from .coordinator import MacBridgeCoordinator


class MacBridgeEntity(CoordinatorEntity[MacBridgeCoordinator]):
    """Shared device info and helpers."""

    _attr_has_entity_name = True

    def __init__(self, coordinator: MacBridgeCoordinator, key: str) -> None:
        super().__init__(coordinator)
        serial = coordinator.config_entry.unique_id
        device = coordinator.data.get("device", {})
        self._attr_unique_id = f"{serial}_{key}"
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, serial)},
            name=device.get("name", "Mac"),
            manufacturer="Apple",
            model=device.get("model"),
            sw_version=f"macOS {device.get('os_version', '')}",
            serial_number=serial,
        )

    @property
    def online(self) -> bool:
        return self.coordinator.data.get("online", True)

    @property
    def stats(self) -> dict[str, Any]:
        return self.coordinator.data.get("stats", {})

    @property
    def media(self) -> dict[str, Any]:
        return self.coordinator.data.get("media", {})
