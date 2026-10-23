import Foundation

/// Routes HTTP requests, executes commands and pushes status to Home Assistant / Homey.
final class Bridge {
    private let stats = SystemStats()
    private let media = MediaController()
    private let config = Config.shared
    private let stateLock = NSLock()
    private var pushTimer: Timer?

    static let commands = [
        "play_pause", "play", "pause", "next", "previous",
        "set_volume", "volume_up", "volume_down", "mute", "unmute", "set_mute",
        "sleep", "display_sleep", "wake_display", "lock", "restart", "shutdown",
        "say", "open_url", "notify",
    ]

    // MARK: Status

    func status() -> [String: Any] {
        stateLock.lock(); defer { stateLock.unlock() }
        let volume = media.volume()
        return [
            "device": SystemControl.deviceInfo(),
            "stats": stats.snapshot(),
            "volume": volume.level,
            "muted": volume.muted,
            "media": media.nowPlaying(),
            "commands": Self.commands,
            "timestamp": Int(Date().timeIntervalSince1970),
        ]
    }

    // MARK: HTTP

    func handle(_ request: HTTPRequest) -> HTTPResponse {
        if request.path == "/api/ping" {
            return .json(["ok": true, "device": SystemControl.deviceInfo()])
        }
        guard request.headers["authorization"] == "Bearer \(config.token)" else {
            return .error("unauthorized", status: 401)
        }

        switch (request.method, request.path) {
        case ("GET", "/api/status"):
            return .json(status())
        case ("POST", "/api/command"):
            guard let body = try? JSONSerialization.jsonObject(with: request.body) as? [String: Any],
                  let command = body["command"] as? String
            else { return .error("body must be JSON: {\"command\": ..., \"value\": ...}", status: 400) }
            do {
                try execute(command, value: body["value"])
            } catch let error as CommandError {
                return .error(error.message, status: error.status)
            } catch {
                return .error("\(error)", status: 500)
            }
            // Give the system a moment, then return fresh state.
            usleep(300_000)
            let state = status()
            DispatchQueue.global().async { self.push(state) }
            return .json(["ok": true, "status": state])
        default:
            return .error("not found", status: 404)
        }
    }

    struct CommandError: Error {
        let message: String
        let status: Int
    }

    func execute(_ command: String, value: Any?) throws {
        NSLog("MacBridge command: \(command) \(String(describing: value))")
        switch command {
        case "play_pause": media.playPause()
        case "play": media.play()
        case "pause": media.pause()
        case "next": media.next()
        case "previous": media.previous()
        case "set_volume":
            guard let level = intValue(value) else { throw CommandError(message: "value 0-100 required", status: 400) }
            media.setVolume(level)
        case "volume_up": media.volumeStep(intValue(value) ?? 6)
        case "volume_down": media.volumeStep(-(intValue(value) ?? 6))
        case "mute": media.setMuted(true)
        case "unmute": media.setMuted(false)
        case "set_mute": media.setMuted(boolValue(value))
        case "sleep": runLater(SystemControl.sleep)
        case "display_sleep": runLater(SystemControl.displaySleep)
        case "wake_display": SystemControl.wakeDisplay()
        case "lock": SystemControl.lock()
        case "restart", "shutdown":
            guard config.allowPowerCommands else {
                throw CommandError(message: "power commands disabled in MacBridge", status: 403)
            }
            runLater(command == "restart" ? SystemControl.restart : SystemControl.shutdown)
        case "say":
            guard let text = value as? String else { throw CommandError(message: "text required", status: 400) }
            DispatchQueue.global().async { SystemControl.say(text) }
        case "open_url":
            guard let url = value as? String else { throw CommandError(message: "url required", status: 400) }
            SystemControl.openURL(url)
        case "notify":
            let dict = value as? [String: Any]
            let message = (dict?["message"] as? String) ?? (value as? String) ?? ""
            SystemControl.notify(title: (dict?["title"] as? String) ?? "Home", message: message)
        default:
            throw CommandError(message: "unknown command \(command)", status: 400)
        }
    }

    /// Lets the HTTP response go out before the Mac goes to sleep / powers off.
    private func runLater(_ action: @escaping () -> Void) {
        DispatchQueue.global().asyncAfter(deadline: .now() + 1, execute: action)
    }

    private func intValue(_ v: Any?) -> Int? {
        if let i = v as? Int { return i }
        if let d = v as? Double { return d < 1 ? Int(d * 100) : Int(d) } // accepts 0.0-1.0 too
        if let s = v as? String { return Int(s) }
        return nil
    }

    private func boolValue(_ v: Any?) -> Bool {
        if let b = v as? Bool { return b }
        if let s = v as? String { return s == "true" || s == "1" }
        return (v as? Int ?? 0) != 0
    }

    // MARK: Push

    func startPushing() {
        pushTimer?.invalidate()
        pushTimer = Timer.scheduledTimer(withTimeInterval: config.pushInterval, repeats: true) { [weak self] _ in
            guard let self, !self.config.pushURLs.isEmpty else { return }
            DispatchQueue.global().async { self.push(self.status()) }
        }
    }

    private func push(_ state: [String: Any]) {
        guard let body = try? JSONSerialization.data(withJSONObject: state) else { return }
        for string in config.pushURLs {
            guard let url = URL(string: string) else { continue }
            var request = URLRequest(url: url, timeoutInterval: 5)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("Bearer \(config.token)", forHTTPHeaderField: "Authorization")
            request.httpBody = body
            URLSession.shared.dataTask(with: request).resume()
        }
    }
}
