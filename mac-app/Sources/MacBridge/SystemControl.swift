import AppKit
import AVFoundation
import CryptoKit
import Foundation
import IOKit
import IOKit.pwr_mgt

/// Power and session commands. Each one does exactly one fixed thing; none takes a path, URL,
/// script or command line from the network.
enum SystemControl {
    static func sleep() { Shell.run("/usr/bin/pmset", ["sleepnow"]) }

    static func displaySleep() { Shell.run("/usr/bin/pmset", ["displaysleepnow"]) }

    /// Wakes the display (the network side of "wake" is Wake-on-LAN, sent by HA/Homey).
    static func wakeDisplay() {
        var id: IOPMAssertionID = 0
        let result = IOPMAssertionDeclareUserActivity("MacBridge wake" as CFString, kIOPMUserActiveLocal, &id)
        if result == kIOReturnSuccess {
            DispatchQueue.global().asyncAfter(deadline: .now() + 5) { IOPMAssertionRelease(id) }
        }
    }

    /// Locks the screen without needing Accessibility permission (no simulated keystrokes).
    static func lock() {
        typealias LockFunction = @convention(c) () -> Int32
        if let handle = dlopen("/System/Library/PrivateFrameworks/login.framework/Versions/Current/login", RTLD_LAZY),
           let symbol = dlsym(handle, "SACLockScreenImmediate") {
            _ = unsafeBitCast(symbol, to: LockFunction.self)()
        } else {
            displaySleep() // locks when "Require password immediately" is on
        }
    }

    /// Asks macOS to restart the normal way (apps get to save and quit).
    static func restart() { sendToLoginWindow("rrst") }

    /// Asks macOS to shut down the normal way (apps get to save and quit).
    static func shutdown() { sendToLoginWindow("shut") }

    /// kAERestart / kAEShutDown to loginwindow, as described in Apple Technical Q&A QA1134.
    private static func sendToLoginWindow(_ eventID: String) {
        let target = NSAppleEventDescriptor(bundleIdentifier: "com.apple.loginwindow")
        let event = NSAppleEventDescriptor(
            eventClass: fourCharCode("aevt"),
            eventID: fourCharCode(eventID),
            targetDescriptor: target,
            returnID: AEReturnID(-1), // kAutoGenerateReturnID
            transactionID: AETransactionID(0) // kAnyTransactionID
        )
        do {
            _ = try event.sendEvent(options: [.noReply], timeout: 10)
        } catch {
            NSLog("MacBridge: \(eventID) failed: \(error)")
        }
    }

    private static func fourCharCode(_ s: String) -> FourCharCode {
        s.utf8.reduce(0) { ($0 << 8) | FourCharCode($1) }
    }

    // MARK: Speech and notifications

    private static let synthesizer = AVSpeechSynthesizer()

    static func say(_ text: String) {
        let utterance = AVSpeechUtterance(string: clean(text, limit: 500))
        if let language = Locale.preferredLanguages.first { utterance.voice = AVSpeechSynthesisVoice(language: language) }
        DispatchQueue.main.async {
            synthesizer.stopSpeaking(at: .immediate)
            synthesizer.speak(utterance)
        }
    }

    /// Strips control characters and caps the length of text from the network.
    static func clean(_ text: String, limit: Int) -> String {
        String(text.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }.prefix(limit).map(Character.init))
    }

    // MARK: Device info

    static func deviceInfo() -> [String: Any] {
        var size = 0
        sysctlbyname("hw.model", nil, &size, nil, 0)
        var model = [CChar](repeating: 0, count: max(size, 1))
        sysctlbyname("hw.model", &model, &size, nil, 0)
        let os = ProcessInfo.processInfo.operatingSystemVersion
        var info: [String: Any] = [
            "id": deviceID(),
            "name": Host.current().localizedName ?? ProcessInfo.processInfo.hostName,
            "model": String(cString: model),
            "os_version": "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)",
            "app_version": appVersion,
        ]
        if let mac = macAddress() { info["mac_address"] = mac } // for Wake-on-LAN
        return info
    }

    static let appVersion = "1.2.0"

    /// Stable, anonymous id for this Mac: a hash of the hardware serial, so the serial number
    /// itself is never shared (not in Bonjour, not in the API).
    static func deviceID() -> String {
        let digest = SHA256.hash(data: Data("MacBridge:\(serialNumber())".utf8))
        return digest.prefix(12).map { String(format: "%02x", $0) }.joined()
    }

    private static func serialNumber() -> String {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPlatformExpertDevice"))
        defer { IOObjectRelease(service) }
        let value = IORegistryEntryCreateCFProperty(service, kIOPlatformSerialNumberKey as CFString, kCFAllocatorDefault, 0)
        return (value?.takeRetainedValue() as? String) ?? ProcessInfo.processInfo.hostName
    }

    /// Hardware address of the first active Ethernet/Wi-Fi interface (en0, en1, …).
    static func macAddress() -> String? {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return nil }
        defer { freeifaddrs(list) }

        var withIPv4 = Set<String>()
        var hardware: [String: String] = [:]
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            guard let addr = ptr.pointee.ifa_addr else { continue }
            let name = String(cString: ptr.pointee.ifa_name)
            guard name.hasPrefix("en") else { continue }
            if addr.pointee.sa_family == UInt8(AF_INET) { withIPv4.insert(name) }
            if addr.pointee.sa_family == UInt8(AF_LINK) {
                addr.withMemoryRebound(to: sockaddr_dl.self, capacity: 1) { sdl in
                    guard sdl.pointee.sdl_alen == 6 else { return }
                    let offset = MemoryLayout<sockaddr_dl>.offset(of: \sockaddr_dl.sdl_data)! + Int(sdl.pointee.sdl_nlen)
                    let base = UnsafeRawPointer(sdl).advanced(by: offset).assumingMemoryBound(to: UInt8.self)
                    hardware[name] = (0..<6).map { String(format: "%02x", base[$0]) }.joined(separator: ":")
                }
            }
        }
        let names = withIPv4.sorted { $0.localizedStandardCompare($1) == .orderedAscending }
        return names.compactMap { hardware[$0] }.first
    }
}
