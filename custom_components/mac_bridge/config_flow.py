"""Config flow for Mac Bridge."""

from __future__ import annotations

from typing import Any

import voluptuous as vol

from homeassistant.config_entries import ConfigEntry, ConfigFlow, ConfigFlowResult, OptionsFlow
from homeassistant.const import CONF_HOST, CONF_PORT, CONF_TOKEN
from homeassistant.core import callback
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from .api import MacBridgeAuthError, MacBridgeClient, MacBridgeError, MacBridgeOutdatedError
from .const import CONF_DEVICE, CONF_SCAN_INTERVAL, DEFAULT_PORT, DEFAULT_SCAN_INTERVAL, DOMAIN


class MacBridgeConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle setup."""

    VERSION = 1

    def __init__(self) -> None:
        self._host: str | None = None
        self._port: int = DEFAULT_PORT
        self._name: str | None = None

    async def _validate(self, host: str, port: int, token: str) -> dict[str, Any]:
        client = MacBridgeClient(async_get_clientsession(self.hass), host, port, token)
        return await client.status()

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                status = await self._validate(
                    user_input[CONF_HOST], user_input[CONF_PORT], user_input[CONF_TOKEN]
                )
            except MacBridgeAuthError:
                errors["base"] = "invalid_auth"
            except MacBridgeOutdatedError:
                errors["base"] = "outdated_mac_app"
            except MacBridgeError:
                errors["base"] = "cannot_connect"
            else:
                device = status.get("device") or {}
                unique_id = device.get("id")
                if not unique_id:
                    errors["base"] = "cannot_connect"
                else:
                    await self.async_set_unique_id(unique_id)
                    self._abort_if_unique_id_configured(updates=user_input)
                    return self.async_create_entry(title=device.get("name", "Mac"), data=user_input)

        schema = vol.Schema(
            {
                vol.Required(CONF_HOST, default=self._host or ""): str,
                vol.Required(CONF_PORT, default=self._port): int,
                vol.Required(CONF_TOKEN): str,
            }
        )
        return self.async_show_form(
            step_id="user",
            data_schema=schema,
            errors=errors,
            description_placeholders={"name": self._name or "Mac"},
        )

    async def async_step_zeroconf(self, discovery_info: ZeroconfServiceInfo) -> ConfigFlowResult:
        device_id = discovery_info.properties.get("id")
        if device_id:
            await self.async_set_unique_id(device_id)
            self._abort_if_unique_id_configured(
                updates={CONF_HOST: discovery_info.host, CONF_PORT: discovery_info.port}
            )
        # Entries created by version 1.0 use the hardware serial as unique id; match those by
        # their remembered device id or host instead of offering the same Mac again.
        for entry in self._async_current_entries(include_ignore=False):
            known_id = (entry.data.get(CONF_DEVICE) or {}).get("id")
            if (device_id and known_id == device_id) or entry.data.get(CONF_HOST) == discovery_info.host:
                return self.async_abort(reason="already_configured")
        self._host = discovery_info.host
        self._port = discovery_info.port or DEFAULT_PORT
        self._name = discovery_info.name.split(".")[0]
        self.context["title_placeholders"] = {"name": self._name}
        return await self.async_step_user()

    async def async_step_reauth(self, entry_data: dict[str, Any]) -> ConfigFlowResult:
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        entry = self._get_reauth_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                await self._validate(entry.data[CONF_HOST], entry.data[CONF_PORT], user_input[CONF_TOKEN])
            except MacBridgeAuthError:
                errors["base"] = "invalid_auth"
            except MacBridgeOutdatedError:
                errors["base"] = "outdated_mac_app"
            except MacBridgeError:
                errors["base"] = "cannot_connect"
            else:
                return self.async_update_reload_and_abort(entry, data_updates=user_input)
        return self.async_show_form(
            step_id="reauth_confirm",
            data_schema=vol.Schema({vol.Required(CONF_TOKEN): str}),
            errors=errors,
        )

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> OptionsFlow:
        return MacBridgeOptionsFlow()


class MacBridgeOptionsFlow(OptionsFlow):
    """Polling interval."""

    async def async_step_init(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        if user_input is not None:
            return self.async_create_entry(data=user_input)
        current = self.config_entry.options.get(CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL)
        return self.async_show_form(
            step_id="init",
            data_schema=vol.Schema(
                {vol.Required(CONF_SCAN_INTERVAL, default=current): vol.All(int, vol.Range(min=2, max=600))}
            ),
        )
