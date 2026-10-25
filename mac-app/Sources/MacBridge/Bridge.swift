import Foundation

/// Routes HTTP requests, executes commands and pushes status to Home Assistant.
final class Bridge {
    private let stats = SystemStats()
    private let media = MediaController()
    let sensors = Sensors()
    let events = EventLog()
    private(set) lazy var notifier = Notifier(events: events)
    private let audio = AudioPlayer()
    private let config = Config.shared
    private let stateLock = NSLock()
    private let nonces = NonceCache()
    private let limiter = FailureLimiter()
    private var pushTimer: Timer?

    /// Most recent rejected request, shown in the menu to help with pairing problems.
    var lastAuthFailure: (reason: String, host: String, date: Date)? {
        failureLock.lock(); defer { failureLock.unlock() }
        return storedAuthFailure
    }
    private var storedAuthFailure: (reason: String, host: String, date: Date)?
    private let failureLock = NSLock()

    /// The complete list of things MacBridge can be asked to do. Anything else is rejected.
    static let commands = [
        "play_pause", "play", "pause", "next", "previous",
        "set_volume", "volume_up", "volume_down", "mute", "unmute", "set_mute",
        "sleep", "display_sleep", "wake", "wake_display", "lock", "restart", "shutdown",
        "say", "notify", "play_audio", "stop_audio",
    ]

    /// Call once on the main thread at launch.
    func start() {
        _ = notifier
        sensors.onStateChange = { [weak self] state in
            guard let self else { return }
            self.events.add("state_change", ["state": state])
            DispatchQueue.global().async { self.push(self.status()) }
        }
        sensors.startObserving()
    }

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
            "sensors": sensors.snapshot(),
            "events": events.recent,
            "commands": Self.commands,
            "power_commands": config.allowPowerCommands,
            "timestamp": Int(Date().timeIntervalSince1970),
        ]
    }

    // MARK: HTTP

    func handle(_ request: HTTPRequest) -> HTTPResponse {
        if request.method == "GET" && request.path == "/api/ping" {
            return .json(["ok": true, "protocol": "MacBridge-v1"])
        }
        if limiter.isBlocked(request.remoteHost) {
            return .error("too many failed attempts", status: 429)
        }
        let nonce: String
        switch authenticate(request) {
        case .success(let value):
            nonce = value
        case .failure(let failure):
            limiter.recordFailure(request.remoteHost)
            failureLock.lock()
            storedAuthFailure = (failure.reason, request.remoteHost, Date())
            failureLock.unlock()
            NSLog("MacBridge: rejected request from \(request.remoteHost): \(failure.reason)")
            return .json(["error": "unauthorized", "reason": failure.reason, "protocol": "MacBridge-v1",
                          "server_time": Int(Date().timeIntervalSince1970)], status: 401)
        }
        var response = route(request)
        response.headers["X-MacBridge-Signature"] = Auth.sign(
            Auth.responseMessage(nonce: nonce, status: response.status, body: response.body), token: config.token
        )
        return response
    }

    struct AuthFailure: Error {
        /// Safe to show the client: says what was wrong, never anything about the token.
        let reason: String
    }

    /// Verifies the request signature, timestamp and nonce. Returns the nonce on success.
    private func authenticate(_ request: HTTPRequest) -> Result<String, AuthFailure> {
        guard let timestamp = request.headers[Auth.timestampHeader],
              let nonce = request.headers[Auth.nonceHeader],
              let signature = request.headers[Auth.signatureHeader]
        else { return .failure(AuthFailure(reason: "unsigned_request")) }
        guard let seconds = TimeInterval(timestamp),
              abs(Date().timeIntervalSince1970 - seconds) <= Auth.maxSkew
        else { return .failure(AuthFailure(reason: "clock_skew")) }
        guard Auth.isValidNonce(nonce) else { return .failure(AuthFailure(reason: "invalid_nonce")) }
        let expected = Auth.sign(
            Auth.requestMessage(method: request.method, target: request.target, timestamp: timestamp, nonce: nonce, body: request.body),
            token: config.token
        )
        guard Auth.matches(signature, expected) else { return .failure(AuthFailure(reason: "wrong_token")) }
        guard nonces.insert(nonce) else { return .failure(AuthFailure(reason: "replayed_request")) }
        return .success(nonce)
    }

    private func route(_ request: HTTPRequest) -> HTTPResponse {
        switch (request.method, request.path) {
        case ("GET", "/api/status"):
            return .json(status())
        case ("POST", "/api/command"):
            guard let body = try? JSONSerialization.jsonObject(with: request.body) as? [String: Any],
                  let command = body["command"] as? String
            else { return .error("body must be JSON: {\"command\": ..., \"value\": ...}", status: 400) }
            do {
                try execute(command, value: body["value"], requester: request.remoteHost)
            } catch let error as CommandError {
                return .error(error.message, status: error.status)
            } catch {
                return .error("command failed", status: 500)
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

    func execute(_ command: String, value: Any?, requester: String) throws {
        guard Self.commands.contains(command) else {
            throw CommandError(message: "unknown command", status: 400)
        }
        NSLog("MacBridge command: \(command)")
        switch command {
        case "play_pause": media.playPause()
        case "play": media.play()
        case "pause": media.pause()
        case "next": media.next()
        case "previous": media.previous()
        case "set_volume":
            guard let level = intValue(value), (0...100).contains(level) else {
                throw CommandError(message: "value 0-100 required", status: 400)
            }
            media.setVolume(level)
        case "volume_up": media.volumeStep(intValue(value) ?? 6)
        case "volume_down": media.volumeStep(-(intValue(value) ?? 6))
        case "mute": media.setMuted(true)
        case "unmute": media.setMuted(false)
        case "set_mute": media.setMuted(boolValue(value))
        case "sleep": runLater(SystemControl.sleep)
        case "display_sleep": runLater(SystemControl.displaySleep)
        case "wake", "wake_display": SystemControl.wakeDisplay()
        case "lock": SystemControl.lock()
        case "restart", "shutdown":
            guard config.allowPowerCommands else {
                throw CommandError(message: "restart/shutdown is turned off in MacBridge", status: 403)
            }
            runLater(command == "restart" ? SystemControl.restart : SystemControl.shutdown)
        case "say":
            guard config.allowSpeechAndNotifications else {
                throw CommandError(message: "speech is turned off in MacBridge", status: 403)
            }
            guard let text = value as? String, !text.isEmpty else { throw CommandError(message: "text required", status: 400) }
            SystemControl.say(text)
        case "notify":
            guard config.allowSpeechAndNotifications else {
                throw CommandError(message: "notifications are turned off in MacBridge", status: 403)
            }
            let dict = value as? [String: Any]
            let message = (dict?["message"] as? String) ?? (value as? String) ?? ""
            guard !message.isEmpty else { throw CommandError(message: "message required", status: 400) }
            let extra = dict?["data"] as? [String: Any]
            let actions = Notifier.parseActions(extra?["actions"] ?? dict?["actions"])
            notifier.show(title: (dict?["title"] as? String) ?? "Home", message: message, actions: actions)
        case "play_audio":
            guard config.allowSpeechAndNotifications else {
                throw CommandError(message: "audio is turned off in MacBridge", status: 403)
            }
            let dict = value as? [String: Any]
            guard let url = (dict?["url"] as? String) ?? (value as? String) else {
                throw CommandError(message: "url required", status: 400)
            }
            let volume = (dict?["volume"] as? NSNumber).map { $0.floatValue }
            do {
                try audio.play(url, requester: requester, volume: volume)
            } catch AudioPlayer.Failure.notAllowed {
                throw CommandError(message: "audio url must point to the computer that sent the request", status: 403)
            } catch {
                throw CommandError(message: "could not play audio", status: 422)
            }
        case "stop_audio":
            audio.stop()
        default:
            throw CommandError(message: "unknown command", status: 400)
        }
    }

    /// Lets the HTTP response go out before the Mac goes to sleep / powers off.
    private func runLater(_ action: @escaping () -> Void) {
        DispatchQueue.global().asyncAfter(deadline: .now() + 1, execute: action)
    }

    private func intValue(_ v: Any?) -> Int? {
        if let n = v as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() {
            let d = n.doubleValue
            return d.isFinite && abs(d) < 1_000 ? Int(d.rounded()) : nil
        }
        if let s = v as? String { return Int(s) }
        return nil
    }

    private func boolValue(_ v: Any?) -> Bool {
        if let b = v as? Bool { return b }
        if let s = v as? String { return s == "true" || s == "1" }
        return false
    }

    // MARK: Push

    func startPushing() {
        pushTimer?.invalidate()
        pushTimer = Timer.scheduledTimer(withTimeInterval: config.pushInterval, repeats: true) { [weak self] _ in
            guard let self, !self.config.pushURLs.isEmpty else { return }
            DispatchQueue.global().async { self.push(self.status()) }
        }
    }

    /// Sends status to the configured webhooks, signed so the receiver can verify it came from this Mac.
    private func push(_ state: [String: Any]) {
        guard let body = try? JSONSerialization.data(withJSONObject: state) else { return }
        for string in config.pushURLs {
            guard let url = URL(string: string) else { continue }
            let timestamp = String(Int(Date().timeIntervalSince1970))
            let nonce = Auth.randomNonce()
            var request = URLRequest(url: url, timeoutInterval: 5)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue(timestamp, forHTTPHeaderField: "X-MacBridge-Timestamp")
            request.setValue(nonce, forHTTPHeaderField: "X-MacBridge-Nonce")
            request.setValue(
                Auth.sign(Auth.pushMessage(timestamp: timestamp, nonce: nonce, body: body), token: config.token),
                forHTTPHeaderField: "X-MacBridge-Signature"
            )
            request.httpBody = body
            URLSession.shared.dataTask(with: request).resume()
        }
    }
}
