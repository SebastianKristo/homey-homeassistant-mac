import AppKit
import Foundation

/// Media and volume control. Uses system media keys (works with any player) and
/// AppleScript for now-playing info from Music and Spotify.
final class MediaController {
    private static let supportedPlayers = ["Spotify", "Music"]

    // MARK: Volume

    func volume() -> (level: Int, muted: Bool) {
        let out = Shell.osascript("set s to get volume settings\nreturn (output volume of s as text) & \",\" & (output muted of s as text)")
        let parts = out.split(separator: ",")
        let level = parts.first.flatMap { Int($0) } ?? 0
        let muted = parts.count > 1 && parts[1] == "true"
        return (level, muted)
    }

    func setVolume(_ level: Int) {
        Shell.osascript("set volume output volume \(max(0, min(100, level)))")
    }

    func setMuted(_ muted: Bool) {
        Shell.osascript("set volume output muted \(muted)")
    }

    func volumeStep(_ delta: Int) {
        setVolume(volume().level + delta)
    }

    // MARK: Playback

    func playPause() {
        if let app = activePlayer() { Shell.osascript("tell application \"\(app)\" to playpause") }
        else { postMediaKey(NX_KEYTYPE_PLAY) }
    }

    func play() {
        if let app = activePlayer() { Shell.osascript("tell application \"\(app)\" to play") }
        else { postMediaKey(NX_KEYTYPE_PLAY) }
    }

    func pause() {
        if let app = activePlayer() { Shell.osascript("tell application \"\(app)\" to pause") }
        else { postMediaKey(NX_KEYTYPE_PLAY) }
    }

    func next() {
        if let app = activePlayer() { Shell.osascript("tell application \"\(app)\" to next track") }
        else { postMediaKey(NX_KEYTYPE_NEXT) }
    }

    func previous() {
        if let app = activePlayer() { Shell.osascript("tell application \"\(app)\" to previous track") }
        else { postMediaKey(NX_KEYTYPE_PREVIOUS) }
    }

    // MARK: Now playing

    func nowPlaying() -> [String: Any] {
        guard let app = activePlayer() else { return ["state": "idle"] }
        let script = """
        tell application "\(app)"
            set st to player state as text
            set t to ""
            set a to ""
            set al to ""
            set d to 0
            try
                set t to name of current track
                set a to artist of current track
                set al to album of current track
                set d to duration of current track
            end try
            set p to 0
            try
                set p to player position
            end try
            return st & "|~|" & t & "|~|" & a & "|~|" & al & "|~|" & (d as text) & "|~|" & (p as text)
        end tell
        """
        let fields = Shell.osascript(script).components(separatedBy: "|~|")
        guard fields.count >= 6 else { return ["state": "idle", "app": app] }

        var duration = Double(fields[4].replacingOccurrences(of: ",", with: ".")) ?? 0
        if app == "Spotify" { duration /= 1000 } // Spotify reports milliseconds
        let state: String
        switch fields[0] {
        case "playing": state = "playing"
        case "paused": state = "paused"
        default: state = "idle"
        }
        return [
            "state": state,
            "app": app,
            "title": fields[1],
            "artist": fields[2],
            "album": fields[3],
            "duration": Int(duration),
            "position": Int(Double(fields[5].replacingOccurrences(of: ",", with: ".")) ?? 0),
        ]
    }

    /// First running player that is playing; otherwise first running player.
    private func activePlayer() -> String? {
        let running = Set(NSWorkspace.shared.runningApplications.compactMap { $0.localizedName })
        let candidates = Self.supportedPlayers.filter { running.contains($0) }
        if let playing = candidates.first(where: {
            Shell.osascript("tell application \"\($0)\" to return player state as text") == "playing"
        }) {
            return playing
        }
        return candidates.first
    }

    // MARK: Media keys

    private func postMediaKey(_ key: Int32) {
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
