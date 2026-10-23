"""Base entity for Mac Bridge."""

from __future__ import annotations

from typing import Any

from homeassistant.helpers.device_registry import CONNECTION_NETWORK_MAC, DeviceInfo
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .const import CONF_DEVICE, DOMAIN
from .coordinator import MacBridgeCoordinator


class MacBridgeEntity(CoordinatorEntity[MacBridgeCoordinator]):
    """Shared device info and helpers."""

    _attr_has_entity_name = True

    def __init__(self, coordinator: MacBridgeCoordinator, key: str) -> None:
        super().__init__(coordinator)
        unique_id = coordinator.config_entry.unique_id
        device = coordinator.data.get("device") or coordinator.config_entry.data.get(CONF_DEVICE) or {}
        self._attr_unique_id = f"{unique_id}_{key}"
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, unique_id)},
            connections={(CONNECTION_NETWORK_MAC, mac)} if (mac := device.get("mac_address")) else set(),
            name=device.get("name", "Mac"),
            manufacturer="Apple",
            model=device.get("model"),
            sw_version=f"macOS {device.get('os_version', '')}",
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
