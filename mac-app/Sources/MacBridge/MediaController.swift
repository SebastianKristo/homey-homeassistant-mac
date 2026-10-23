import AppKit
import Foundation

/// Media and volume control. Talks to Music and Spotify over AppleScript (found by bundle id, so
/// it also works when macOS shows Music under a localized name such as "Musikk"), and falls back
/// to the system media keys for other players.
final class MediaController {
    private struct Player {
        let name: String
        let bundleID: String
        let durationInMilliseconds: Bool
    }

    // Fixed list: the only apps MacBridge ever sends Apple Events to (besides loginwindow).
    private static let players = [
        Player(name: "Spotify", bundleID: "com.spotify.client", durationInMilliseconds: true),
        Player(name: "Music", bundleID: "com.apple.Music", durationInMilliseconds: false),
    ]

    // MARK: Volume

    func volume() -> (level: Int, muted: Bool) {
        let out = Shell.osascript("set s to get volume settings\nreturn (output volume of s as text) & \",\" & (output muted of s as text)").output
        let parts = out.split(separator: ",")
        let level = parts.first.flatMap { Int($0) } ?? 0
        let muted = parts.count > 1 && parts[1] == "true"
        return (level, muted)
    }

    func setVolume(_ level: Int) {
        Shell.osascript("set volume output volume \(max(0, min(100, level)))")
    }

    func setMuted(_ muted: Bool) {
        Shell.osascript("set volume output muted \(muted ? "true" : "false")")
    }

    func volumeStep(_ delta: Int) {
        setVolume(volume().level + max(-25, min(25, delta)))
    }

    // MARK: Playback

    func playPause() { control("playpause", key: NX_KEYTYPE_PLAY) }
    func play() { control("play", key: NX_KEYTYPE_PLAY) }
    func pause() { control("pause", key: NX_KEYTYPE_PLAY) }
    func next() { control("next track", key: NX_KEYTYPE_NEXT) }
    func previous() { control("previous track", key: NX_KEYTYPE_PREVIOUS) }

    private func control(_ verb: String, key: Int32) {
        if let player = activePlayer() {
            let result = Shell.osascript("tell application id \"\(player.bundleID)\" to \(verb)")
            if result.status == 0 { return }
            NSLog("MacBridge: \(player.name) refused '\(verb)': \(result.error)")
        }
        postMediaKey(key)
    }

    // MARK: Now playing

    func nowPlaying() -> [String: Any] {
        guard let player = activePlayer() else { return ["state": "idle"] }
        let artwork = player.bundleID == "com.spotify.client" ? "set w to artwork url of current track" : ""
        let script = """
        tell application id "\(player.bundleID)"
            set sep to character id 31
            set st to player state as text
            set t to ""
            set a to ""
            set al to ""
            set d to 0
            set w to ""
            try
                set t to name of current track
                set a to artist of current track
                set al to album of current track
                set d to duration of current track
                \(artwork)
            end try
            set p to 0
            try
                set p to player position
            end try
            return st & sep & t & sep & a & sep & al & sep & (d as text) & sep & (p as text) & sep & w
        end tell
        """
        let result = Shell.osascript(script)
        if result.status != 0 {
            // -1743: the user has not allowed MacBridge to control this app (Privacy → Automation).
            let error = result.error.contains("-1743") ? "not_authorized" : "unavailable"
            return ["state": "idle", "app": player.name, "error": error]
        }
        let fields = result.output.components(separatedBy: "\u{1F}")
        guard fields.count >= 7 else { return ["state": "idle", "app": player.name] }

        var duration = number(fields[4])
        if player.durationInMilliseconds { duration /= 1000 }
        let state: String
        switch fields[0] {
        case "playing": state = "playing"
        case "paused": state = "paused"
        default: state = "idle"
        }
        var info: [String: Any] = [
            "state": state,
            "app": player.name,
            "title": fields[1],
            "artist": fields[2],
            "album": fields[3],
            "duration": Int(duration),
            "position": Int(number(fields[5])),
        ]
        if fields[6].hasPrefix("https://") { info["artwork_url"] = fields[6] }
        return info
    }

    /// Prompts for Automation permission for each supported player that is running.
    func requestPermissions() {
        for player in runningPlayers() {
            Shell.osascript("tell application id \"\(player.bundleID)\" to return player state as text")
        }
        if !CGPreflightPostEventAccess() { CGRequestPostEventAccess() }
    }

    private func number(_ text: String) -> Double {
        Double(text.replacingOccurrences(of: ",", with: ".")) ?? 0 // some locales use a decimal comma
    }

    private func runningPlayers() -> [Player] {
        let running = Set(NSWorkspace.shared.runningApplications.compactMap(\.bundleIdentifier))
        return Self.players.filter { running.contains($0.bundleID) }
    }

    /// First running player that is playing; otherwise first running player.
    private func activePlayer() -> Player? {
        let candidates = runningPlayers()
        if candidates.count > 1, let playing = candidates.first(where: {
            Shell.osascript("tell application id \"\($0.bundleID)\" to return player state as text").output == "playing"
        }) {
            return playing
        }
        return candidates.first
    }

    // MARK: Media keys

    /// Controls any other player (browser, Podcasts, …). Needs the optional Accessibility permission.
    private func postMediaKey(_ key: Int32) {
        guard CGPreflightPostEventAccess() else {
            NSLog("MacBridge: no supported player running and media keys not allowed (Accessibility)")
            return
        }
        DispatchQueue.main.async {
            for down in [true, false] {
                let flags = NSEvent.ModifierFlags(rawValue: down ? 0xA00 : 0xB00)
                let data1 = (Int(key) << 16) | ((down ? 0xA : 0xB) << 8)
                let event = NSEvent.otherEvent(
                    with: .systemDefined, location: .zero, modifierFlags: flags,
                    timestamp: 0, windowNumber: 0, context: nil,
                    subtype: 8, data1: data1, data2: -1
                )
                event?.cgEvent?.post(tap: .cghidEventTap)
            }
        }
    }
}

// From IOKit/hidsystem/ev_keymap.h
private let NX_KEYTYPE_PLAY: Int32 = 16
private let NX_KEYTYPE_NEXT: Int32 = 17
private let NX_KEYTYPE_PREVIOUS: Int32 = 18
