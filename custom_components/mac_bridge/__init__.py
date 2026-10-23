"""Mac Bridge: control and monitor a Mac running the MacBridge app."""

from __future__ import annotations

import hmac

from aiohttp import web

from homeassistant.components import webhook
from homeassistant.config_entries import ConfigEntry, ConfigEntryState
from homeassistant.const import CONF_HOST, CONF_PORT, CONF_TOKEN, Platform
from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.exceptions import ServiceValidationError
from homeassistant.helpers import config_validation as cv, device_registry as dr
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.typing import ConfigType
import voluptuous as vol

from .api import MacBridgeClient
from .const import CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL, DOMAIN
from .coordinator import MacBridgeCoordinator

PLATFORMS = [
    Platform.BINARY_SENSOR,
    Platform.BUTTON,
    Platform.MEDIA_PLAYER,
    Platform.NUMBER,
    Platform.SENSOR,
]

type MacBridgeConfigEntry = ConfigEntry[MacBridgeCoordinator]


CONFIG_SCHEMA = cv.config_entry_only_config_schema(DOMAIN)

SERVICE_SCHEMA = vol.Schema(
    {
        vol.Required("device_id"): cv.string,
        vol.Required("command"): cv.string,
        vol.Optional("value"): vol.Any(str, int, float, bool, dict),
    }
)


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Register the send_command service (say, notify, open_url, ...)."""

    async def send_command(call: ServiceCall) -> None:
        device = dr.async_get(hass).async_get(call.data["device_id"])
        entries = [
            entry
            for entry_id in (device.config_entries if device else ())
            if (entry := hass.config_entries.async_get_entry(entry_id))
            and entry.domain == DOMAIN
            and entry.state is ConfigEntryState.LOADED
        ]
        if not entries:
            raise ServiceValidationError("Device is not a loaded Mac Bridge device")
        entry = entries[0]
        await entry.runtime_data.async_command(call.data["command"], call.data.get("value"))

    hass.services.async_register(DOMAIN, "send_command", send_command, schema=SERVICE_SCHEMA)
    return True


def webhook_id_for(entry: ConfigEntry) -> str:
    """Webhook id the Mac app can push status to (/api/webhook/<id>)."""
    return f"{DOMAIN}_{entry.unique_id}"


async def async_setup_entry(hass: HomeAssistant, entry: MacBridgeConfigEntry) -> bool:
    client = MacBridgeClient(
        async_get_clientsession(hass),
        entry.data[CONF_HOST],
        entry.data[CONF_PORT],
        entry.data[CONF_TOKEN],
    )
    coordinator = MacBridgeCoordinator(
        hass, entry, client, entry.options.get(CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL)
    )
    await coordinator.async_config_entry_first_refresh()
    entry.runtime_data = coordinator

    async def handle_push(hass: HomeAssistant, webhook_id: str, request: web.Request):
        auth = request.headers.get("Authorization", "")
        if not hmac.compare_digest(auth, f"Bearer {entry.data[CONF_TOKEN]}"):
            return web.Response(status=401)
        coordinator.async_set_updated_data(await request.json())
        return web.Response(status=200)

    webhook.async_register(
        hass, DOMAIN, entry.title, webhook_id_for(entry), handle_push, allowed_methods=["POST"]
    )
    entry.async_on_unload(lambda: webhook.async_unregister(hass, webhook_id_for(entry)))
    entry.async_on_unload(entry.add_update_listener(_async_reload))

    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


async def _async_reload(hass: HomeAssistant, entry: ConfigEntry) -> None:
    await hass.config_entries.async_reload(entry.entry_id)


async def async_unload_entry(hass: HomeAssistant, entry: MacBridgeConfigEntry) -> bool:
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
