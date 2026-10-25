"""Constants for Mac Bridge."""

DOMAIN = "mac_bridge"
DEFAULT_PORT = 8765
DEFAULT_SCAN_INTERVAL = 10
CONF_SCAN_INTERVAL = "scan_interval"
# Last known device info (name, model, MAC address), so the Mac can be woken after a HA restart.
CONF_DEVICE = "device"

# Everything the Mac app accepts. The Mac enforces this list too.
COMMANDS = [
    "play_pause",
    "play",
    "pause",
    "next",
    "previous",
    "set_volume",
    "volume_up",
    "volume_down",
    "mute",
    "unmute",
    "set_mute",
    "sleep",
    "display_sleep",
    "wake",
    "wake_display",
    "lock",
    "restart",
    "shutdown",
    "say",
    "notify",
    "play_audio",
    "stop_audio",
]

# Bus events fired when the computer reports something happened.
EVENT_NOTIFICATION_ACTION = "mac_bridge_notification_action"
EVENT_STATE_CHANGE = "mac_bridge_state_change"
