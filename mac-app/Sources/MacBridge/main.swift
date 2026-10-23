import AppKit

/// Menu bar app: runs the HTTP API and shows connection details.
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let bridge = Bridge()
    private var server: HTTPServer?
    private var statusItem: NSStatusItem!
    private let config = Config.shared

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "desktopcomputer", accessibilityDescription: "MacBridge")
        startServer()
        bridge.startPushing()
        rebuildMenu()
    }

    private func startServer() {
        server?.stop()
        let info = SystemControl.deviceInfo()
        let server = HTTPServer(port: config.port) { [bridge] in bridge.handle($0) }
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
        menu.addItem(withTitle: "MacBridge – port \(config.port)", action: nil, keyEquivalent: "")
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
        statusItem.menu = menu
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
