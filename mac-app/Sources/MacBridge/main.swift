import AppKit

/// Menu bar app: runs the HTTP API and shows connection details.
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let bridge = Bridge()
    private var server: HTTPServer?
    private var statusItem: NSStatusItem!
    private let config = Config.shared

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "desktopcomputer", accessibilityDescription: "MacBridge")
        rebuildMenu()
        quitOtherInstances {
            self.startServer()
            self.bridge.startPushing()
        }
    }

    /// An older MacBridge that is still running would keep the port (and answer with the old
    /// protocol), so ask it to quit before starting.
    private func quitOtherInstances(then start: @escaping () -> Void) {
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .filter { $0 != NSRunningApplication.current }
        guard !others.isEmpty else { start(); return }
        NSLog("MacBridge: quitting \(others.count) other running instance(s)")
        others.forEach { $0.terminate() }
        var waited = 0.0
        func check() {
            if others.allSatisfy(\.isTerminated) || waited >= 5 {
                if waited >= 5 { others.forEach { $0.forceTerminate() } }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: start)
                return
            }
            waited += 0.25
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: check)
        }
        check()
    }

    private func startServer() {
        server?.stop()
        let info = SystemControl.deviceInfo()
        let server = HTTPServer(port: config.port) { [bridge] in bridge.handle($0) }
        server.onFailure = { [weak self] error in
            DispatchQueue.main.async {
                self?.alert("MacBridge could not open port \(self?.config.port ?? 0): \(error). Another program (or an old MacBridge) may be using it. Quit it, or choose another port.")
            }
        }
        do {
            try server.start(
                serviceName: info["name"] as? String ?? "Mac",
                txt: ["id": SystemControl.deviceID(), "model": info["model"] as? String ?? "", "version": "2"]
            )
        } catch {
            NSLog("MacBridge failed to start: \(error)")
        }
        self.server = server
    }

    private func rebuildMenu() {
        let menu = NSMenu()
        populate(menu)
        menu.delegate = self
        statusItem.menu = menu
    }

    /// Refreshes the menu each time it opens (e.g. to show the last rejected request).
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        populate(menu)
    }

    private func populate(_ menu: NSMenu) {
        menu.addItem(withTitle: "MacBridge \(SystemControl.appVersion) – port \(config.port)", action: nil, keyEquivalent: "")
        menu.addItem(withTitle: "Device ID: \(SystemControl.deviceID())", action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(item("Copy token", #selector(copyToken)))
        menu.addItem(item("Regenerate token", #selector(regenerateToken)))
        menu.addItem(item("Change port…", #selector(changePort)))
        menu.addItem(item("Push URLs (webhooks)…", #selector(editPushURLs)))
        let power = item("Allow restart/shutdown", #selector(togglePower))
        power.state = config.allowPowerCommands ? .on : .off
        menu.addItem(power)
        let speech = item("Allow speech and notifications", #selector(toggleSpeech))
        speech.state = config.allowSpeechAndNotifications ? .on : .off
        menu.addItem(speech)
        menu.addItem(item("Request permissions", #selector(requestPermissions)))
        menu.addItem(.separator())
        menu.addItem(item("Quit", #selector(quit), key: "q"))
        if let failure = bridge.lastAuthFailure {
            let time = DateFormatter.localizedString(from: failure.date, dateStyle: .none, timeStyle: .short)
            let hint: String
            switch failure.reason {
            case "unsigned_request": hint = "old Homey/HA app, update it"
            case "wrong_token": hint = "wrong token"
            case "clock_skew": hint = "clocks differ by more than 1 min"
            default: hint = failure.reason
            }
            menu.addItem(.separator())
            menu.addItem(withTitle: "Last rejected: \(failure.host) at \(time) (\(hint))", action: nil, keyEquivalent: "")
        }
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    private var clipboardClearWork: DispatchWorkItem?

    /// Copies the token for this Mac only (no Universal Clipboard) and clears it again after a minute.
    @objc private func copyToken() {
        let pasteboard = NSPasteboard.general
        let token = config.token
        _ = pasteboard.prepareForNewContents(with: .currentHostOnly)
        pasteboard.setString(token, forType: .string)
        pasteboard.setString("", forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        let changeCount = pasteboard.changeCount
        clipboardClearWork?.cancel()
        let work = DispatchWorkItem {
            if pasteboard.changeCount == changeCount { pasteboard.clearContents() }
        }
        clipboardClearWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 60, execute: work)
    }

    @objc private func regenerateToken() {
        config.regenerateToken()
        copyToken()
        alert("New token copied to clipboard (cleared after 1 minute). Update Home Assistant and Homey.")
    }

    @objc private func changePort() {
        guard let value = prompt("HTTP port", value: String(config.port)), let port = UInt16(value) else { return }
        config.port = port
        startServer()
        rebuildMenu()
    }

    @objc private func editPushURLs() {
        guard let value = prompt("Push URLs (http/https), comma separated. Status is POSTed every \(Int(config.pushInterval))s, signed with the token.",
                                 value: config.pushURLs.joined(separator: ", ")) else { return }
        let urls = value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let rejected = urls.filter { !Config.isAllowedPushURL($0) }
        config.pushURLs = urls
        if !rejected.isEmpty { alert("Ignored (must be http:// or https://): \(rejected.joined(separator: ", "))") }
    }

    @objc private func togglePower() {
        config.allowPowerCommands.toggle()
        rebuildMenu()
    }

    @objc private func toggleSpeech() {
        config.allowSpeechAndNotifications.toggle()
        rebuildMenu()
    }

    /// Triggers the macOS Automation prompts for Music/Spotify (only for players that are running),
    /// and the optional permission to send media keys to other players.
    @objc private func requestPermissions() {
        DispatchQueue.global().async { MediaController().requestPermissions() }
    }

    @objc private func quit() { NSApp.terminate(nil) }

    private func alert(_ text: String) {
        let alert = NSAlert()
        alert.messageText = text
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    private func prompt(_ text: String, value: String) -> String? {
        let alert = NSAlert()
        alert.messageText = text
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        field.stringValue = value
        alert.accessoryView = field
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn ? field.stringValue : nil
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
