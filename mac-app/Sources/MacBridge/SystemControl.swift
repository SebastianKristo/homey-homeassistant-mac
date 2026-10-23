import AppKit
import IOKit
import Foundation

/// Power and session commands.
enum SystemControl {
    static func sleep() { Shell.run("/usr/bin/pmset", ["sleepnow"]) }

    static func displaySleep() { Shell.run("/usr/bin/pmset", ["displaysleepnow"]) }

    static func wakeDisplay() { Shell.run("/usr/bin/caffeinate", ["-u", "-t", "2"]) }

    static func lock() {
        // Ctrl+Cmd+Q locks the screen.
        Shell.osascript("tell application \"System Events\" to keystroke \"q\" using {control down, command down}")
    }

    static func restart() {
        Shell.osascript("tell application \"System Events\" to restart")
    }

    static func shutdown() {
        Shell.osascript("tell application \"System Events\" to shut down")
    }

    static func say(_ text: String) {
        Shell.run("/usr/bin/say", [text], timeout: 60)
    }

    static func openURL(_ string: String) {
        if let url = URL(string: string) {
            DispatchQueue.main.async { NSWorkspace.shared.open(url) }
        }
    }

    static func notify(title: String, message: String) {
        let esc = { (s: String) in s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") }
        Shell.osascript("display notification \"\(esc(message))\" with title \"\(esc(title))\"")
    }

    static func deviceInfo() -> [String: Any] {
        var size = 0
        sysctlbyname("hw.model", nil, &size, nil, 0)
        var model = [CChar](repeating: 0, count: max(size, 1))
        sysctlbyname("hw.model", &model, &size, nil, 0)
        let os = ProcessInfo.processInfo.operatingSystemVersion
        return [
            "name": Host.current().localizedName ?? ProcessInfo.processInfo.hostName,
            "model": String(cString: model),
            "os_version": "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)",
            "serial": serialNumber(),
            "app_version": "1.0.0",
        ]
    }

    private static func serialNumber() -> String {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPlatformExpertDevice"))
        defer { IOObjectRelease(service) }
        let value = IORegistryEntryCreateCFProperty(service, kIOPlatformSerialNumberKey as CFString, kCFAllocatorDefault, 0)
        return (value?.takeRetainedValue() as? String) ?? ProcessInfo.processInfo.hostName
    }
}
