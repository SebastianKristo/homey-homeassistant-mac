import AppKit
import Foundation
import UserNotifications

/// Things that happened on the Mac that Home Assistant / Homey should react to
/// (a notification button was clicked, the Mac went to sleep, …). Kept in memory, newest last.
final class EventLog {
    private let lock = NSLock()
    private var events: [[String: Any]] = []
    private var nextID = Int(Date().timeIntervalSince1970 * 1000) // unique across restarts

    func add(_ type: String, _ data: [String: Any] = [:]) {
        lock.lock(); defer { lock.unlock() }
        var event = data
        event["id"] = nextID
        event["type"] = type
        event["at"] = Int(Date().timeIntervalSince1970)
        nextID += 1
        events.append(event)
        if events.count > 50 { events.removeFirst(events.count - 50) }
    }

    var recent: [[String: Any]] {
        lock.lock(); defer { lock.unlock() }
        return events
    }
}

/// macOS notifications with optional buttons. A clicked button becomes a "notification_action"
/// event; a button may open an https/http link, but only when the user clicks it.
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    private let events: EventLog
    private let center = UNUserNotificationCenter.current()
    private let lock = NSLock()
    /// Categories (button sets) of recent notifications; macOS needs them registered up front.
    private var categories: [String: UNNotificationCategory] = [:]
    private var categoryOrder: [String] = []
    /// Links behind "openuri" buttons, keyed by notification id + action.
    private var links: [String: URL] = [:]

    struct Action {
        let action: String
        let title: String
        let uri: URL?
    }

    init(events: EventLog) {
        self.events = events
        super.init()
        center.delegate = self
    }

    func requestPermission() {
        center.requestAuthorization(options: [.alert, .sound]) { granted, error in
            if !granted { NSLog("MacBridge: notifications not allowed (\(error?.localizedDescription ?? "denied"))") }
        }
    }

    /// Parses `[{"action": "...", "title": "...", "uri": "https://..."}]` from the network.
    /// At most 4 buttons; only http(s) links are kept.
    static func parseActions(_ value: Any?) -> [Action] {
        guard let list = value as? [[String: Any]] else { return [] }
        return list.prefix(4).compactMap { item in
            guard let action = item["action"] as? String,
                  case let id = SystemControl.clean(action, limit: 64), !id.isEmpty
            else { return nil }
            let title = SystemControl.clean(item["title"] as? String ?? id, limit: 40)
            var uri: URL?
            if let string = item["uri"] as? String, let url = URL(string: string),
               let scheme = url.scheme?.lowercased(), scheme == "https" || scheme == "http", url.host != nil {
                uri = url
            }
            return Action(action: id, title: title.isEmpty ? id : title, uri: uri)
        }
    }

    func show(title: String, message: String, actions: [Action]) {
        let id = UUID().uuidString
        let content = UNMutableNotificationContent()
        content.title = SystemControl.clean(title, limit: 100)
        content.body = SystemControl.clean(message, limit: 500)
        content.sound = .default
        content.userInfo = ["macbridge_id": id]

        if !actions.isEmpty {
            let categoryID = "macbridge.\(id)"
            let buttons = actions.map { UNNotificationAction(identifier: $0.action, title: $0.title, options: []) }
            let category = UNNotificationCategory(identifier: categoryID, actions: buttons, intentIdentifiers: [])
            lock.lock()
            categories[categoryID] = category
            categoryOrder.append(categoryID)
            while categoryOrder.count > 20 { categories[categoryOrder.removeFirst()] = nil }
            for action in actions { if let uri = action.uri { links["\(id)|\(action.action)"] = uri } }
            if links.count > 80 { links.removeAll() }
            let all = Set(categories.values)
            lock.unlock()
            center.setNotificationCategories(all)
            content.categoryIdentifier = categoryID
        }
        center.add(UNNotificationRequest(identifier: id, content: content, trigger: nil)) { error in
            if let error { NSLog("MacBridge: notification failed: \(error.localizedDescription)") }
        }
    }

    // Show banners even though MacBridge is a menu bar app.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        defer { completionHandler() }
        let action = response.actionIdentifier
        guard action != UNNotificationDismissActionIdentifier, action != UNNotificationDefaultActionIdentifier else { return }
        let id = response.notification.request.content.userInfo["macbridge_id"] as? String ?? ""
        lock.lock()
        let link = links["\(id)|\(action)"]
        lock.unlock()
        if let link { DispatchQueue.main.async { NSWorkspace.shared.open(link) } }
        events.add("notification_action", [
            "action": action,
            "title": response.notification.request.content.title,
        ])
    }
}
