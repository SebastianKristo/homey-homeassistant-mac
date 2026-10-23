import Foundation

/// Persistent settings stored in UserDefaults.
final class Config {
    static let shared = Config()
    private let defaults = UserDefaults.standard

    var port: UInt16 {
        get { UInt16(defaults.integer(forKey: "port")).nonZero ?? 8765 }
        set { defaults.set(Int(newValue), forKey: "port") }
    }

    /// Shared secret used by Home Assistant / Homey (Authorization: Bearer <token>).
    var token: String {
        if let t = defaults.string(forKey: "token"), !t.isEmpty { return t }
        let t = Config.generateToken()
        defaults.set(t, forKey: "token")
        return t
    }

    func regenerateToken() {
        defaults.set(Config.generateToken(), forKey: "token")
    }

    /// Optional URLs that receive a POST with the full status JSON on every change/interval
    /// (e.g. a Home Assistant webhook or the Homey app webhook).
    var pushURLs: [String] {
        get { defaults.stringArray(forKey: "pushURLs") ?? [] }
        set { defaults.set(newValue, forKey: "pushURLs") }
    }

    var pushInterval: TimeInterval {
        get { let v = defaults.double(forKey: "pushInterval"); return v > 0 ? v : 10 }
        set { defaults.set(newValue, forKey: "pushInterval") }
    }

    /// Restart/shutdown can be disabled for safety.
    var allowPowerCommands: Bool {
        get { defaults.object(forKey: "allowPowerCommands") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "allowPowerCommands") }
    }

    private static func generateToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 24)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return bytes.map { String(format: "%02x", $0) }.joined()
    }
}

private extension UInt16 {
    var nonZero: UInt16? { self == 0 ? nil : self }
}
