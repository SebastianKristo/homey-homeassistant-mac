"""Sensors, notify service, events and text-to-speech playback."""

from copy import deepcopy
from unittest.mock import AsyncMock, patch

from pytest_homeassistant_custom_component.common import MockConfigEntry, async_capture_events

from homeassistant.const import CONF_HOST, CONF_PORT, CONF_TOKEN
from homeassistant.core import HomeAssistant

from custom_components.mac_bridge.const import DOMAIN, EVENT_NOTIFICATION_ACTION

STATUS = {
    "device": {"id": "abc123", "name": "Studio", "model": "Mac16,10", "os_version": "26.0", "mac_address": "aa:bb:cc:dd:ee:ff"},
    "stats": {"cpu_usage": 12.5, "memory_usage": 40.0, "uptime": 100},
    "volume": 30,
    "muted": False,
    "media": {"state": "idle"},
    "sensors": {
        "active_app": "Safari",
        "idle_seconds": 120,
        "last_active": 1_760_000_000,
        "logged_user": "sebastian",
        "logged_users": ["sebastian"],
        "session_locked": False,
        "display_on": True,
        "gpu_usage": 7,
        "microphone_active": True,
        "webcam_active": False,
        "printers": ["Brother"],
        "processes": {"Zoom": True},
    },
    "events": [{"id": 1, "type": "state_change", "state": "wake", "at": 1}],
}


async def _setup(hass: HomeAssistant, status: dict):
    entry = MockConfigEntry(
        domain=DOMAIN, unique_id="abc123", title="Studio",
        data={CONF_HOST: "192.168.1.20", CONF_PORT: 8765, CONF_TOKEN: "t" * 64},
    )
    entry.add_to_hass(hass)
    client = AsyncMock()
    client.status.return_value = status
    client.command.return_value = {"ok": True, "status": status}
    with patch("custom_components.mac_bridge.MacBridgeClient", return_value=client):
        assert await hass.config_entries.async_setup(entry.entry_id)
        await hass.async_block_till_done()
    return entry, client


async def test_sensors_created(hass: HomeAssistant) -> None:
    await _setup(hass, deepcopy(STATUS))
    assert hass.states.get("sensor.studio_active_app").state == "Safari"
    assert hass.states.get("sensor.studio_gpu_usage").state == "7"
    assert hass.states.get("sensor.studio_printers").attributes["names"] == ["Brother"]
    assert hass.states.get("binary_sensor.studio_microphone_in_use").state == "on"
    assert hass.states.get("binary_sensor.studio_locked").state == "off"
    assert hass.states.get("binary_sensor.studio_zoom_running").state == "on"
    # Not reported by this computer -> not created.
    assert hass.states.get("sensor.studio_pending_windows_updates") is None


async def test_notify_with_actions_and_event(hass: HomeAssistant) -> None:
    status = deepcopy(STATUS)
    entry, client = await _setup(hass, status)
    assert hass.services.has_service("notify", "studio")
    await hass.services.async_call(
        "notify", "studio",
        {"message": "Vacuum full", "title": "Hi", "data": {"actions": [{"action": "pause", "title": "Pause"}]}},
        blocking=True,
    )
    client.command.assert_called_with(
        "notify", {"message": "Vacuum full", "title": "Hi", "data": {"actions": [{"action": "pause", "title": "Pause"}]}}
    )

    events = async_capture_events(hass, EVENT_NOTIFICATION_ACTION)
    status["events"].append({"id": 2, "type": "notification_action", "action": "pause", "title": "Hi", "at": 2})
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()
    assert len(events) == 1
    assert events[0].data["action"] == "pause"
    # The same event is not fired twice.
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()
    assert len(events) == 1


async def test_play_media_url(hass: HomeAssistant) -> None:
    await hass.config.async_update(internal_url="http://192.168.1.2:8123")
    _, client = await _setup(hass, deepcopy(STATUS))
    await hass.services.async_call(
        "media_player", "play_media",
        {"entity_id": "media_player.studio", "media_content_id": "/api/tts_proxy/abc.mp3", "media_content_type": "music"},
        blocking=True,
    )
    command, value = client.command.call_args.args
    assert command == "play_audio"
    assert value["url"].startswith("http://192.168.1.2:8123/api/tts_proxy/abc.mp3")
