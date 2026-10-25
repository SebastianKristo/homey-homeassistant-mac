"""Media player for Mac Bridge."""

from __future__ import annotations

from typing import Any

from homeassistant.components import media_source
from homeassistant.components.media_player import (
    BrowseMedia,
    MediaPlayerDeviceClass,
    MediaPlayerEntity,
    MediaPlayerEntityFeature,
    MediaPlayerState,
    MediaType,
    async_process_play_media_url,
)
from homeassistant.exceptions import HomeAssistantError
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.util import dt as dt_util

from . import MacBridgeConfigEntry
from .entity import MacBridgeEntity

STATES = {
    "playing": MediaPlayerState.PLAYING,
    "paused": MediaPlayerState.PAUSED,
    "idle": MediaPlayerState.IDLE,
}


async def async_setup_entry(
    hass: HomeAssistant, entry: MacBridgeConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    async_add_entities([MacMediaPlayer(entry.runtime_data)])


class MacMediaPlayer(MacBridgeEntity, MediaPlayerEntity):
    _attr_name = None
    _attr_device_class = MediaPlayerDeviceClass.SPEAKER
    _attr_media_content_type = MediaType.MUSIC
    _attr_supported_features = (
        MediaPlayerEntityFeature.PLAY
        | MediaPlayerEntityFeature.PAUSE
        | MediaPlayerEntityFeature.STOP
        | MediaPlayerEntityFeature.NEXT_TRACK
        | MediaPlayerEntityFeature.PREVIOUS_TRACK
        | MediaPlayerEntityFeature.VOLUME_SET
        | MediaPlayerEntityFeature.VOLUME_MUTE
        | MediaPlayerEntityFeature.VOLUME_STEP
        | MediaPlayerEntityFeature.TURN_OFF
        | MediaPlayerEntityFeature.TURN_ON
        | MediaPlayerEntityFeature.PLAY_MEDIA
        | MediaPlayerEntityFeature.BROWSE_MEDIA
        | MediaPlayerEntityFeature.MEDIA_ANNOUNCE
    )

    def __init__(self, coordinator) -> None:
        super().__init__(coordinator, "media_player")
        self._last_position = self.media.get("position")
        self._position_updated_at = dt_util.utcnow()

    @property
    def available(self) -> bool:
        return True

    @property
    def state(self) -> MediaPlayerState:
        if not (self.coordinator.last_update_success and self.online):
            return MediaPlayerState.OFF
        return STATES.get(self.media.get("state", "idle"), MediaPlayerState.ON)

    @property
    def volume_level(self) -> float | None:
        volume = self.coordinator.data.get("volume")
        return volume / 100 if volume is not None else None

    @property
    def is_volume_muted(self) -> bool:
        return bool(self.coordinator.data.get("muted"))

    @property
    def media_title(self) -> str | None:
        return self.media.get("title") or None

    @property
    def media_artist(self) -> str | None:
        return self.media.get("artist") or None

    @property
    def media_album_name(self) -> str | None:
        return self.media.get("album") or None

    @property
    def media_duration(self) -> int | None:
        return self.media.get("duration") or None

    @property
    def media_position(self) -> int | None:
        return self.media.get("position")

    @property
    def media_position_updated_at(self):
        return self._position_updated_at

    @property
    def media_image_url(self) -> str | None:
        url = self.media.get("artwork_url")
        return url if isinstance(url, str) and url.startswith("https://") else None

    @property
    def media_image_remotely_accessible(self) -> bool:
        return False  # proxied through Home Assistant

    def _handle_coordinator_update(self) -> None:
        position = self.media.get("position")
        if position != self._last_position or self.media.get("state") != "playing":
            self._position_updated_at = dt_util.utcnow()
        self._last_position = position
        super()._handle_coordinator_update()

    @property
    def app_name(self) -> str | None:
        return self.media.get("app")

    async def async_media_play(self) -> None:
        await self.coordinator.async_command("play")

    async def async_media_pause(self) -> None:
        await self.coordinator.async_command("pause")

    async def async_media_stop(self) -> None:
        await self.coordinator.async_command("pause")

    async def async_media_next_track(self) -> None:
        await self.coordinator.async_command("next")

    async def async_media_previous_track(self) -> None:
        await self.coordinator.async_command("previous")

    async def async_set_volume_level(self, volume: float) -> None:
        await self.coordinator.async_command("set_volume", round(volume * 100))

    async def async_mute_volume(self, mute: bool) -> None:
        await self.coordinator.async_command("set_mute", mute)

    async def async_volume_up(self) -> None:
        await self.coordinator.async_command("volume_up")

    async def async_volume_down(self) -> None:
        await self.coordinator.async_command("volume_down")

    async def async_play_media(self, media_type: MediaType | str, media_id: str, **kwargs: Any) -> None:
        """Play an audio clip (e.g. text-to-speech) from Home Assistant on the computer.

        The computer only downloads from the address the request came from, i.e. this
        Home Assistant, so the URL must be served by Home Assistant itself.
        """
        if media_source.is_media_source_id(media_id):
            item = await media_source.async_resolve_media(self.hass, media_id, self.entity_id)
            media_id = item.url
        elif not media_id.startswith("/"):
            raise HomeAssistantError("Only media from Home Assistant (media source or TTS) can be played")
        url = async_process_play_media_url(self.hass, media_id)
        await self.coordinator.async_command("play_audio", {"url": url})

    async def async_browse_media(
        self, media_content_type: MediaType | str | None = None, media_content_id: str | None = None
    ) -> BrowseMedia:
        return await media_source.async_browse_media(
            self.hass, media_content_id, content_filter=lambda item: item.media_content_type.startswith("audio/")
        )

    async def async_turn_off(self) -> None:
        await self.coordinator.async_command("sleep")

    async def async_turn_on(self) -> None:
        await self.coordinator.async_wake()
