import Foundation
import Security

/// Persistent settings. Non-secret settings live in UserDefaults; the token lives in the Keychain.
final class Config {
    static let shared = Config()
    private let defaults = UserDefaults.standard
    private let lock = NSLock()
    private var cachedToken: String?

    var port: UInt16 {
        get { UInt16(defaults.integer(forKey: "port")).nonZero ?? 8765 }
        set { defaults.set(Int(newValue), forKey: "port") }
    }

    /// Shared secret used by Home Assistant / Homey to sign requests. Never sent over the network.
    var token: String {
        lock.lock(); defer { lock.unlock() }
        if let cachedToken { return cachedToken }
        // Migrate tokens stored in UserDefaults by version 1.0.0.
        if let legacy = defaults.string(forKey: "token"), !legacy.isEmpty {
            if Keychain.write(legacy) { defaults.removeObject(forKey: "token") }
            cachedToken = legacy
            return legacy
        }
        if let stored = Keychain.read(), !stored.isEmpty {
            cachedToken = stored
            return stored
        }
        let fresh = Config.generateToken()
        _ = Keychain.write(fresh)
        cachedToken = fresh
        return fresh
    }

    func regenerateToken() {
        let fresh = Config.generateToken()
        lock.lock(); defer { lock.unlock() }
        _ = Keychain.write(fresh)
        cachedToken = fresh
    }

    /// Optional http(s) URLs that receive a signed POST with the status JSON on every interval
    /// (e.g. a Home Assistant webhook).
    var pushURLs: [String] {
        get { (defaults.stringArray(forKey: "pushURLs") ?? []).filter(Config.isAllowedPushURL) }
        set { defaults.set(newValue.filter(Config.isAllowedPushURL), forKey: "pushURLs") }
    }

    static func isAllowedPushURL(_ string: String) -> Bool {
        guard let url = URL(string: string), let scheme = url.scheme?.lowercased(), url.host != nil else { return false }
        return scheme == "http" || scheme == "https"
    }

    var pushInterval: TimeInterval {
        get { let v = defaults.double(forKey: "pushInterval"); return v > 0 ? max(v, 2) : 10 }
        set { defaults.set(newValue, forKey: "pushInterval") }
    }

    /// Restart/shutdown can be turned off from the menu bar.
    var allowPowerCommands: Bool {
        get { defaults.object(forKey: "allowPowerCommands") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "allowPowerCommands") }
    }

    /// Speech and notifications can be turned off from the menu bar.
    var allowSpeechAndNotifications: Bool {
        get { defaults.object(forKey: "allowSpeechAndNotifications") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "allowSpeechAndNotifications") }
    }

    /// Opt-in: report names of connected Bluetooth devices (macOS asks for Bluetooth permission).
    var reportBluetoothDevices: Bool {
        get { defaults.bool(forKey: "reportBluetoothDevices") }
        set { defaults.set(newValue, forKey: "reportBluetoothDevices") }
    }

    /// App/process names whose running state is reported. Chosen locally in the menu bar only;
    /// nothing on the network can change this list.
    var watchedProcesses: [String] {
        get { defaults.stringArray(forKey: "watchedProcesses") ?? [] }
        set {
            let clean = newValue.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty && $0.count <= 100 }
            defaults.set(Array(clean.prefix(20)), forKey: "watchedProcesses")
        }
    }

    private static func generateToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        precondition(status == errSecSuccess, "no secure random numbers available")
        return bytes.map { String(format: "%02x", $0) }.joined()
    }
}

/// Stores the token as a generic password, readable only on this Mac after first unlock.
enum Keychain {
    private static let service = "MacBridge"
    private static let account = "api-token"

    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    static func read() -> String? {
        var q = query
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    @discardableResult
    static func write(_ value: String) -> Bool {
        let data = Data(value.utf8)
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecSuccess { return true }
        var add = query
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess { NSLog("MacBridge: could not store token in Keychain (\(status))") }
        return status == errSecSuccess
    }
}

private extension UInt16 {
    var nonZero: UInt16? { self == 0 ? nil : self }
}
