Mac Bridge lets Homey control and monitor a Mac or a Windows PC on your local network.

Install the free MacBridge app on the computer (see the project page): the menu bar app on a Mac, or MacBridge for Windows in the notification area on a PC. Copy the token from its menu and add the computer in Homey as a Mac mini, MacBook Pro or Windows PC. Homey finds it automatically over mDNS/Bonjour, or you can enter its address manually.

Features:
- Media: play/pause, next/previous, now playing, volume and mute (Music and Spotify on Mac; Spotify and other players on Windows).
- Power: wake (Wake-on-LAN), sleep, display off/on, lock screen, restart and shut down.
- Statistics: CPU, memory, disk, battery, and on Mac also load and thermal state, with Insights.
- Flows: speak text, show a notification, and triggers when CPU goes above a threshold or the computer goes offline/online.

All requests are signed with the token, which is never sent over the network. The computer only accepts a fixed list of commands, and restart/shut down can be turned off in its menu.
