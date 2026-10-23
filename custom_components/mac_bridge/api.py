"""HTTP client for the MacBridge app."""

from __future__ import annotations

from typing import Any

import aiohttp


class MacBridgeError(Exception):
    """Generic API error."""


class MacBridgeAuthError(MacBridgeError):
    """Invalid token."""


class MacBridgeClient:
    """Talks to the MacBridge menu bar app."""

    def __init__(self, session: aiohttp.ClientSession, host: str, port: int, token: str) -> None:
        self._session = session
        self._base = f"http://{host}:{port}/api"
        self._headers = {"Authorization": f"Bearer {token}"}

    async def _request(self, method: str, path: str, json: dict | None = None) -> dict[str, Any]:
        try:
            async with self._session.request(
                method,
                f"{self._base}{path}",
                json=json,
                headers=self._headers,
                timeout=aiohttp.ClientTimeout(total=15),
            ) as resp:
                if resp.status == 401:
                    raise MacBridgeAuthError("Invalid token")
                data = await resp.json(content_type=None)
                if resp.status != 200:
                    raise MacBridgeError(data.get("error", f"HTTP {resp.status}"))
                return data
        except (aiohttp.ClientError, TimeoutError) as err:
            raise MacBridgeError(str(err)) from err

    async def status(self) -> dict[str, Any]:
        return await self._request("GET", "/status")

    async def command(self, command: str, value: Any = None) -> dict[str, Any]:
        body: dict[str, Any] = {"command": command}
        if value is not None:
            body["value"] = value
        return await self._request("POST", "/command", body)
