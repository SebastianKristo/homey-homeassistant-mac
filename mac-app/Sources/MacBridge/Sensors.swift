import AppKit
import CoreAudio
import CoreGraphics
import CoreMediaIO
import Foundation
import IOBluetooth
import IOKit

/// Read-only sensors (who is using the Mac, what is in front, mic/camera in use, …).
/// Every value comes from a fixed system API; nothing here takes a query, path or command
/// from the network, and nothing reads window contents, screens or location.
final class Sensors {
    private let config = Config.shared
    private let lock = NSLock()
    private var lastStateChange: (state: String, at: Date)?
    private var screenLocked = false
    private var observers: [NSObjectProtocol] = []

    /// Called whenever the system state changes (sleep, wake, lock, …), so status can be pushed.
    var onStateChange: ((String) -> Void)?

    // MARK: Snapshot

    func snapshot() -> [String: Any] {
        var s: [String: Any] = [:]
        s["active_app"] = NSWorkspace.shared.frontmostApplication?.localizedName ?? ""
        let idle = idleSeconds()
        s["idle_seconds"] = idle
        s["last_active"] = Int(Date().timeIntervalSince1970 - Double(idle))
        s["logged_user"] = NSUserName()
        s["logged_users"] = loggedUsers()
        s["session_locked"] = isScreenLocked()
        s["display_on"] = CGDisplayIsAsleep(CGMainDisplayID()) == 0
        if let gpu = gpuUsage() { s["gpu_usage"] = gpu }
        s["microphone_active"] = isDefaultInputRunning()
        s["webcam_active"] = isAnyCameraRunning()
        if let output = defaultDeviceName(kAudioHardwarePropertyDefaultOutputDevice) { s["audio_output"] = output }
        if let input = defaultDeviceName(kAudioHardwarePropertyDefaultInputDevice) { s["audio_input"] = input }
        let displays = displayInfo()
        s["displays"] = displays.count
        if let main = displays.main { s["display_resolution"] = main }
        s["accent_color"] = accentColor()
        s["printers"] = NSPrinter.printerNames
        if let network = primaryNetwork() {
            s["ip_address"] = network.ip
            s["network_interface"] = network.interface
        }
        if config.reportBluetoothDevices { s["bluetooth_devices"] = connectedBluetoothDevices() }
        if !config.watchedProcesses.isEmpty { s["processes"] = watchedProcesses() }
        lock.lock()
        if let change = lastStateChange {
            s["last_state_change"] = change.state
            s["last_state_change_at"] = Int(change.at.timeIntervalSince1970)
        }
        lock.unlock()
        return s
    }

    // MARK: State changes (sleep, wake, lock, display)

    /// Must be called on the main thread.
    func startObserving() {
        let workspace = NSWorkspace.shared.notificationCenter
        let states: [(Notification.Name, String)] = [
            (NSWorkspace.willSleepNotification, "sleep"),
            (NSWorkspace.didWakeNotification, "wake"),
            (NSWorkspace.screensDidSleepNotification, "display_off"),
            (NSWorkspace.screensDidWakeNotification, "display_on"),
            (NSWorkspace.willPowerOffNotification, "shutdown"),
            (NSWorkspace.sessionDidResignActiveNotification, "user_switched_out"),
            (NSWorkspace.sessionDidBecomeActiveNotification, "user_switched_in"),
        ]
        for (name, state) in states {
            observers.append(workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.record(state)
            })
        }
        let distributed = DistributedNotificationCenter.default()
        for (name, state) in [("com.apple.screenIsLocked", "locked"), ("com.apple.screenIsUnlocked", "unlocked")] {
            observers.append(distributed.addObserver(forName: Notification.Name(name), object: nil, queue: .main) { [weak self] _ in
                self?.lock.lock()
                self?.screenLocked = state == "locked"
                self?.lock.unlock()
                self?.record(state)
            })
        }
    }

    private func record(_ state: String) {
        lock.lock()
        lastStateChange = (state, Date())
        lock.unlock()
        onStateChange?(state)
    }

    private func isScreenLocked() -> Bool {
        if let session = CGSessionCopyCurrentDictionary() as? [String: Any],
           let locked = session["CGSSessionScreenIsLocked"] as? Bool {
            return locked
        }
        lock.lock(); defer { lock.unlock() }
        return screenLocked
    }

    // MARK: User activity

    private func idleSeconds() -> Int {
        guard let anyInput = CGEventType(rawValue: ~0) else { return 0 }
        let seconds = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyInput)
        return seconds.isFinite ? Int(seconds) : 0
    }

    private func loggedUsers() -> [String] {
        var names = Set<String>()
        setutxent()
        defer { endutxent() }
        while let entry = getutxent() {
            guard entry.pointee.ut_type == USER_PROCESS else { continue }
            let name = withUnsafeBytes(of: entry.pointee.ut_user) { raw in
                String(decoding: raw.prefix(while: { $0 != 0 }), as: UTF8.self)
            }
            if !name.isEmpty { names.insert(name) }
        }
        return names.sorted()
    }

    // MARK: GPU

    /// GPU utilisation in percent (Apple silicon and most discrete GPUs report it, no permission needed).
    private func gpuUsage() -> Int? {
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOAccelerator"), &iterator) == KERN_SUCCESS
        else { return nil }
        defer { IOObjectRelease(iterator) }
        var best: Int?
        var service = IOIteratorNext(iterator)
        while service != 0 {
            if let stats = IORegistryEntryCreateCFProperty(service, "PerformanceStatistics" as CFString, kCFAllocatorDefault, 0)?
                .takeRetainedValue() as? [String: Any],
               let utilisation = (stats["Device Utilization %"] ?? stats["GPU Activity(%)"]) as? Int {
                best = max(best ?? 0, utilisation)
            }
            IOObjectRelease(service)
            service = IOIteratorNext(iterator)
        }
        return best
    }

    // MARK: Audio, microphone and camera

    private func defaultDevice(_ selector: AudioObjectPropertySelector) -> AudioObjectID? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var device = AudioObjectID(0)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device)
        return status == noErr && device != 0 ? device : nil
    }

    private func defaultDeviceName(_ selector: AudioObjectPropertySelector) -> String? {
        guard let device = defaultDevice(selector) else { return nil }
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioObjectPropertyName, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var name: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &name) == noErr, let name else { return nil }
        return name.takeRetainedValue() as String
    }

    /// True while any app records from the default microphone.
    private func isDefaultInputRunning() -> Bool {
        guard let device = defaultDevice(kAudioHardwarePropertyDefaultInputDevice) else { return false }
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceIsRunningSomewhere,
            mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var running: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(device, &address, 0, nil, &size, &running) == noErr && running != 0
    }

    /// True while any app uses a camera.
    private func isAnyCameraRunning() -> Bool {
        var address = CMIOObjectPropertyAddress(
            mSelector: CMIOObjectPropertySelector(kCMIOHardwarePropertyDevices),
            mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
            mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        let system = CMIOObjectID(kCMIOObjectSystemObject)
        guard CMIOObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return false }
        var devices = [CMIOObjectID](repeating: 0, count: Int(size) / MemoryLayout<CMIOObjectID>.size)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(system, &address, 0, nil, size, &used, &devices) == noErr else { return false }

        var running = CMIOObjectPropertyAddress(
            mSelector: CMIOObjectPropertySelector(kCMIODevicePropertyDeviceIsRunningSomewhere),
            mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
            mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        for device in devices {
            var value: UInt32 = 0
            var valueUsed: UInt32 = 0
            if CMIOObjectGetPropertyData(device, &running, 0, nil, UInt32(MemoryLayout<UInt32>.size), &valueUsed, &value) == noErr,
               value != 0 {
                return true
            }
        }
        return false
    }

    // MARK: Displays, appearance, network

    private func displayInfo() -> (count: Int, main: String?) {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success else { return (0, nil) }
        let main = CGMainDisplayID()
        guard let mode = CGDisplayCopyDisplayMode(main) else { return (Int(count), nil) }
        return (Int(count), "\(mode.width)x\(mode.height)")
    }

    private func accentColor() -> String {
        guard let color = NSColor.controlAccentColor.usingColorSpace(.sRGB) else { return "" }
        let c = [color.redComponent, color.greenComponent, color.blueComponent]
            .map { Int(($0 * 255).rounded()).clamped(0, 255) }
        return String(format: "#%02x%02x%02x", c[0], c[1], c[2])
    }

    /// IPv4 address of the first active Ethernet/Wi-Fi interface (en0, en1, …).
    private func primaryNetwork() -> (interface: String, ip: String)? {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return nil }
        defer { freeifaddrs(list) }
        var found: [(String, String)] = []
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            guard let addr = ptr.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) else { continue }
            let name = String(cString: ptr.pointee.ifa_name)
            guard name.hasPrefix("en") else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                found.append((name, String(cString: host)))
            }
        }
        return found.sorted { $0.0.localizedStandardCompare($1.0) == .orderedAscending }.first.map { ($0.0, $0.1) }
    }

    // MARK: Opt-in sensors (turned on in the menu bar)

    /// Names of connected Bluetooth devices. macOS asks for Bluetooth permission the first time.
    private func connectedBluetoothDevices() -> [String] {
        let paired = IOBluetoothDevice.pairedDevices() as? [IOBluetoothDevice] ?? []
        return paired.filter { $0.isConnected() }.compactMap { $0.name }.sorted()
    }

    /// Whether each process name chosen in the menu bar is running.
    private func watchedProcesses() -> [String: Bool] {
        let running = NSWorkspace.shared.runningApplications.flatMap { app -> [String] in
            [app.localizedName, app.executableURL?.lastPathComponent, app.bundleIdentifier].compactMap { $0?.lowercased() }
        }
        let names = Set(running)
        var result: [String: Bool] = [:]
        for name in config.watchedProcesses { result[name] = names.contains(name.lowercased()) }
        return result
    }
}

private extension Int {
    func clamped(_ low: Int, _ high: Int) -> Int { Swift.min(Swift.max(self, low), high) }
}
