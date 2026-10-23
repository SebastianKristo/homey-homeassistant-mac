"""HTTP client for the MacBridge app.

Requests are signed with HMAC-SHA256 (protocol "MacBridge-v1"), so the token itself is never sent
over the network, and every response is checked against the Mac's signature.
"""

from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import secrets
import socket
import time
from typing import Any

import aiohttp

MAX_SKEW = 60
MAX_RESPONSE_BYTES = 256 * 1024


class MacBridgeError(Exception):
    """Generic API error."""


class MacBridgeAuthError(MacBridgeError):
    """Invalid token or signature."""


def _sha256(body: bytes) -> str:
    return hashlib.sha256(body).hexdigest()


def sign(token: str, message: str) -> str:
    """HMAC-SHA256 hex signature."""
    return hmac.new(token.encode(), message.encode(), hashlib.sha256).hexdigest()


def request_message(method: str, target: str, timestamp: str, nonce: str, body: bytes) -> str:
    return f"MacBridge-v1\n{method}\n{target}\n{timestamp}\n{nonce}\n{_sha256(body)}"


def response_message(nonce: str, status: int, body: bytes) -> str:
    return f"MacBridge-v1-response\n{nonce}\n{status}\n{_sha256(body)}"


def push_message(timestamp: str, nonce: str, body: bytes) -> str:
    return f"MacBridge-v1-push\n{timestamp}\n{nonce}\n{_sha256(body)}"


class MacBridgeOutdatedError(MacBridgeError):
    """The Mac runs MacBridge 1.0, which does not understand signed requests."""


def _raise_auth_error(raw: bytes) -> None:
    try:
        body = json.loads(raw)
    except ValueError:
        body = {}
    if not isinstance(body, dict) or "protocol" not in body:
        raise MacBridgeOutdatedError("MacBridge on the Mac is an old version")
    if body.get("reason") == "clock_skew":
        raise MacBridgeError("The clocks on Home Assistant and the Mac differ by more than a minute")
    if body.get("reason") == "replayed_request":
        raise MacBridgeError("Request rejected as a replay")
    raise MacBridgeAuthError("Invalid token")


class MacBridgeClient:
    """Talks to the MacBridge menu bar app."""

    def __init__(self, session: aiohttp.ClientSession, host: str, port: int, token: str) -> None:
        self._session = session
        self._host = host
        self._port = port
        self._token = token

    async def _request(self, method: str, path: str, payload: dict | None = None) -> dict[str, Any]:
        target = f"/api{path}"
        body = json.dumps(payload).encode() if payload is not None else b""
        timestamp = str(int(time.time()))
        nonce = secrets.token_hex(16)
        headers = {
            "X-MacBridge-Timestamp": timestamp,
            "X-MacBridge-Nonce": nonce,
            "X-MacBridge-Signature": sign(
                self._token, request_message(method, target, timestamp, nonce, body)
            ),
        }
        if payload is not None:
            headers["Content-Type"] = "application/json"
        try:
            async with self._session.request(
                method,
                f"http://{self._host}:{self._port}{target}",
                data=body if payload is not None else None,
                headers=headers,
                allow_redirects=False,
                timeout=aiohttp.ClientTimeout(total=15),
            ) as resp:
                raw = await resp.content.read(MAX_RESPONSE_BYTES + 1)
                if resp.status == 401:
                    _raise_auth_error(raw)
                if len(raw) > MAX_RESPONSE_BYTES:
                    raise MacBridgeError("Response too large")
                expected = sign(self._token, response_message(nonce, resp.status, raw))
                if not hmac.compare_digest(resp.headers.get("X-MacBridge-Signature", ""), expected):
                    raise MacBridgeAuthError("Response not signed by this Mac")
                data = json.loads(raw or b"{}")
                if not isinstance(data, dict):
                    raise MacBridgeError("Unexpected response")
                if resp.status != 200:
                    raise MacBridgeError(str(data.get("error", f"HTTP {resp.status}")))
                return data
        except (aiohttp.ClientError, TimeoutError, ValueError) as err:
            raise MacBridgeError(str(err)) from err

    async def status(self) -> dict[str, Any]:
        return await self._request("GET", "/status")

    async def command(self, command: str, value: Any = None) -> dict[str, Any]:
        body: dict[str, Any] = {"command": command}
        if value is not None:
            body["value"] = value
        return await self._request("POST", "/command", body)


def verify_push(token: str, headers: Any, body: bytes) -> str | None:
    """Checks a signed status push from the Mac. Returns the nonce if it is valid."""
    timestamp = headers.get("X-MacBridge-Timestamp", "")
    nonce = headers.get("X-MacBridge-Nonce", "")
    signature = headers.get("X-MacBridge-Signature", "")
    try:
        if abs(time.time() - int(timestamp)) > MAX_SKEW:
            return None
    except ValueError:
        return None
    if not (16 <= len(nonce) <= 64 and nonce.isalnum()):
        return None
    if not hmac.compare_digest(signature, sign(token, push_message(timestamp, nonce, body))):
        return None
    return nonce


def _magic_packet(mac: str) -> bytes:
    digits = mac.replace(":", "").replace("-", "")
    if len(digits) != 12:
        raise ValueError("invalid MAC address")
    return b"\xff" * 6 + bytes.fromhex(digits) * 16


async def async_wake_on_lan(mac: str, host: str | None = None) -> None:
    """Sends a Wake-on-LAN magic packet (UDP port 9) to the broadcast address and the Mac's IP."""
    packet = _magic_packet(mac)

    def _send() -> None:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            for target in ("255.255.255.255", host):
                if target:
                    try:
                        sock.sendto(packet, (target, 9))
                    except OSError:
                        pass

    await asyncio.get_running_loop().run_in_executor(None, _send)
