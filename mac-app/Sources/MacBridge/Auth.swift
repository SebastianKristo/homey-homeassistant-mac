import CryptoKit
import Foundation

/// Request signing (protocol "MacBridge-v1").
///
/// The token never travels over the network. Every request carries a timestamp, a random nonce
/// and `HMAC-SHA256(token, canonical request)`. The Mac answers with a signature over the nonce
/// and the response body, so clients can tell a real MacBridge from an impostor on the LAN.
/// Status pushes to webhooks are signed the same way.
enum Auth {
    static let timestampHeader = "x-macbridge-timestamp"
    static let nonceHeader = "x-macbridge-nonce"
    static let signatureHeader = "x-macbridge-signature"

    /// Requests older/newer than this are rejected (clock skew + replay window).
    static let maxSkew: TimeInterval = 60

    static func requestMessage(method: String, target: String, timestamp: String, nonce: String, body: Data) -> String {
        "MacBridge-v1\n\(method)\n\(target)\n\(timestamp)\n\(nonce)\n\(sha256Hex(body))"
    }

    static func responseMessage(nonce: String, status: Int, body: Data) -> String {
        "MacBridge-v1-response\n\(nonce)\n\(status)\n\(sha256Hex(body))"
    }

    static func pushMessage(timestamp: String, nonce: String, body: Data) -> String {
        "MacBridge-v1-push\n\(timestamp)\n\(nonce)\n\(sha256Hex(body))"
    }

    static func sign(_ message: String, token: String) -> String {
        let key = SymmetricKey(data: Data(token.utf8))
        return hex(HMAC<SHA256>.authenticationCode(for: Data(message.utf8), using: key))
    }

    /// Constant-time comparison of two hex signatures.
    static func matches(_ a: String, _ b: String) -> Bool {
        let x = Array(a.lowercased().utf8), y = Array(b.lowercased().utf8)
        guard x.count == y.count, !x.isEmpty else { return false }
        var diff: UInt8 = 0
        for i in 0..<x.count { diff |= x[i] ^ y[i] }
        return diff == 0
    }

    static func isValidNonce(_ nonce: String) -> Bool {
        (16...64).contains(nonce.count) && nonce.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) }
    }

    static func randomNonce() -> String {
        var bytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return hex(bytes)
    }

    static func sha256Hex(_ data: Data) -> String { hex(SHA256.hash(data: data)) }

    private static func hex<S: Sequence>(_ bytes: S) -> String where S.Element == UInt8 {
        bytes.map { String(format: "%02x", $0) }.joined()
    }
}

/// Remembers nonces inside the replay window so a captured request cannot be sent again.
final class NonceCache {
    private var seen: [String: Date] = [:]
    private let lock = NSLock()
    private let limit = 10_000

    /// Returns false if the nonce was already used.
    func insert(_ nonce: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        if seen.count > limit / 2 { seen = seen.filter { $0.value > now } }
        guard seen.count < limit else { return false } // flooded: fail closed
        if let expiry = seen[nonce], expiry > now { return false }
        seen[nonce] = now.addingTimeInterval(Auth.maxSkew * 2)
        return true
    }
}

/// Blocks addresses that keep sending bad signatures.
final class FailureLimiter {
    private var failures: [String: [Date]] = [:]
    private var blockedUntil: [String: Date] = [:]
    private let lock = NSLock()

    func isBlocked(_ host: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let until = blockedUntil[host] else { return false }
        if until > Date() { return true }
        blockedUntil[host] = nil
        return false
    }

    func recordFailure(_ host: String) {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        var list = (failures[host] ?? []).filter { now.timeIntervalSince($0) < 60 }
        list.append(now)
        failures[host] = list
        if list.count >= 10 {
            blockedUntil[host] = now.addingTimeInterval(300)
            failures[host] = nil
            NSLog("MacBridge: blocking \(host) for 5 minutes after repeated authentication failures")
        }
        if failures.count > 1000 { failures.removeAll() }
    }
}
