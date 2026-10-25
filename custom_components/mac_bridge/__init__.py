"""Mac Bridge: control and monitor a Mac running the MacBridge app."""

from __future__ import annotations

import json
import time

from aiohttp import web

from homeassistant.components import webhook
from homeassistant.config_entries import ConfigEntry, ConfigEntryState
from homeassistant.const import CONF_HOST, CONF_PORT, CONF_TOKEN, Platform
from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.exceptions import ServiceValidationError
from homeassistant.helpers import config_validation as cv, device_registry as dr, discovery
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.typing import ConfigType
import voluptuous as vol

from .api import MAX_SKEW, MacBridgeClient, verify_push
from .const import COMMANDS, CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL, DOMAIN
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

# Notification buttons: at most 4; "uri" (http/https only) is opened when the button is clicked.
NOTIFY_ACTIONS = vol.All(
    [
        {
            vol.Required("action"): vol.All(str, vol.Length(min=1, max=64)),
            vol.Optional("title"): vol.All(str, vol.Length(max=40)),
            vol.Optional("uri"): vol.All(cv.url, vol.Length(max=2000)),
        }
    ],
    vol.Length(max=4),
)

SERVICE_SCHEMA = vol.Schema(
    {
        vol.Required("device_id"): cv.string,
        vol.Required("command"): vol.In(COMMANDS),
        vol.Optional("value"): vol.Any(
            vol.All(str, vol.Length(max=500)),
            int,
            float,
            bool,
            {
                vol.Optional("title"): vol.All(str, vol.Length(max=100)),
                vol.Required("message"): vol.All(str, vol.Length(max=500)),
                vol.Optional("data"): {vol.Optional("actions"): NOTIFY_ACTIONS},
            },
            {vol.Required("url"): vol.All(str, vol.Length(max=2000)), vol.Optional("volume"): vol.Coerce(float)},
        ),
    }
)


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Register the send_command service (say, notify, ...)."""

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
        coordinator = entries[0].runtime_data
        if call.data["command"] == "wake":
            await coordinator.async_wake()
        else:
            await coordinator.async_command(call.data["command"], call.data.get("value"))

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

    seen_nonces: dict[str, float] = {}

    async def handle_push(hass: HomeAssistant, webhook_id: str, request: web.Request):
        """Status pushed by the Mac. Only accepted with a valid, fresh signature."""
        if request.content_length is None or request.content_length > 256 * 1024:
            return web.Response(status=413)
        body = await request.read()
        nonce = verify_push(entry.data[CONF_TOKEN], request.headers, body)
        now = time.time()
        for old in [n for n, t in seen_nonces.items() if now - t > 2 * MAX_SKEW]:
            del seen_nonces[old]
        if nonce is None or nonce in seen_nonces:
            return web.Response(status=401)
        seen_nonces[nonce] = now
        try:
            data = json.loads(body)
        except ValueError:
            return web.Response(status=400)
        if not isinstance(data, dict) or not isinstance(data.get("device"), dict):
            return web.Response(status=400)
        coordinator.ingest(data)
        coordinator.async_set_updated_data(data)
        return web.Response(status=200)

    webhook.async_register(
        hass,
        DOMAIN,
        entry.title,
        webhook_id_for(entry),
        handle_push,
        local_only=True,
        allowed_methods=["POST"],
    )
    entry.async_on_unload(lambda: webhook.async_unregister(hass, webhook_id_for(entry)))
    options = dict(entry.options)

    async def _async_reload(hass: HomeAssistant, updated: ConfigEntry) -> None:
        # Only reload for option changes, not when the remembered device info is updated.
        if dict(updated.options) != options:
            await hass.config_entries.async_reload(updated.entry_id)

    entry.async_on_unload(entry.add_update_listener(_async_reload))

    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    # notify.<computer name>, with optional buttons (legacy notify services support "data").
    hass.async_create_task(
        discovery.async_load_platform(
            hass, Platform.NOTIFY, DOMAIN, {"entry_id": entry.entry_id, "name": entry.title}, {}
        )
    )
    return True


async def async_unload_entry(hass: HomeAssistant, entry: MacBridgeConfigEntry) -> bool:
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
