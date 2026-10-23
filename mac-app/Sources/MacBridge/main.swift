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
                txt: ["id": info["serial"] as? String ?? "", "model": info["model"] as? String ?? "", "version": "1"]
            )
        } catch {
            NSLog("MacBridge failed to start: \(error)")
        }
        self.server = server
    }

    private func rebuildMenu() {
        let menu = NSMenu()
        menu.addItem(withTitle: "MacBridge – port \(config.port)", action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(item("Copy token", #selector(copyToken)))
        menu.addItem(item("Regenerate token", #selector(regenerateToken)))
        menu.addItem(item("Change port…", #selector(changePort)))
        menu.addItem(item("Push URLs (webhooks)…", #selector(editPushURLs)))
        let power = item("Allow restart/shutdown", #selector(togglePower))
        power.state = config.allowPowerCommands ? .on : .off
        menu.addItem(power)
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

    @objc private func copyToken() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(config.token, forType: .string)
    }

    @objc private func regenerateToken() {
        config.regenerateToken()
        copyToken()
        alert("New token copied to clipboard. Update Home Assistant and Homey.")
    }

    @objc private func changePort() {
        guard let value = prompt("HTTP port", value: String(config.port)), let port = UInt16(value) else { return }
        config.port = port
        startServer()
        rebuildMenu()
    }

    @objc private func editPushURLs() {
        guard let value = prompt("Push URLs, comma separated (status is POSTed every \(Int(config.pushInterval))s)",
                                 value: config.pushURLs.joined(separator: ", ")) else { return }
        config.pushURLs = value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    @objc private func togglePower() {
        config.allowPowerCommands.toggle()
        rebuildMenu()
    }

    /// Triggers the macOS Automation/Accessibility prompts up front.
    @objc private func requestPermissions() {
        DispatchQueue.global().async {
            Shell.osascript("tell application \"System Events\" to return name of first process")
            _ = MediaController().nowPlaying()
        }
        let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
        AXIsProcessTrustedWithOptions(options)
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
