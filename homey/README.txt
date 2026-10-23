Mac Bridge lets Homey control and monitor a Mac on your local network.

Install the free MacBridge menu bar app on your Mac (see the project page), copy the token from its menu and add your Mac in Homey. Homey finds the Mac automatically over Bonjour, or you can enter its address manually.

Features:
- Media: play/pause, next/previous, now playing, volume and mute (Music and Spotify).
- Power: wake (Wake-on-LAN), sleep, display off/on, lock screen, restart and shut down.
- Statistics: CPU, memory, disk, load, battery and thermal state, with Insights.
- Flows: speak text, show a notification, and triggers when CPU goes above a threshold or the Mac goes offline/online.

All requests are signed with the token, which is never sent over the network. The Mac app only accepts a fixed list of commands, and restart/shut down can be turned off in its menu.
