import Foundation
import Network

struct HTTPRequest {
    let method: String
    /// Request target as sent (path + query). This is what gets signed.
    let target: String
    let path: String
    let headers: [String: String]
    let body: Data
    /// Remote IP address, used for rate limiting.
    let remoteHost: String
}

struct HTTPResponse {
    var status: Int
    var body: Data
    var headers: [String: String] = [:]

    static func json(_ object: Any, status: Int = 200) -> HTTPResponse {
        let data = (try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])) ?? Data("{}".utf8)
        return HTTPResponse(status: status, body: data)
    }

    static func error(_ message: String, status: Int) -> HTTPResponse {
        json(["error": message], status: status)
    }
}

/// Minimal HTTP/1.1 server built on Network.framework, advertised over Bonjour as `_macbridge._tcp`.
///
/// Hardening: only accepts connections from the local network (private, link-local and loopback
/// addresses), limits concurrent connections, header/body size and how long a client may take.
final class HTTPServer {
    typealias Handler = (HTTPRequest) -> HTTPResponse

    static let maxHeaderBytes = 8 * 1024
    static let maxBodyBytes = 16 * 1024
    static let maxConnections = 16
    static let requestTimeout: TimeInterval = 10

    private let port: NWEndpoint.Port
    private let handler: Handler
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "macbridge.http", attributes: .concurrent)
    private let countLock = NSLock()
    private var activeConnections = 0

    init(port: UInt16, handler: @escaping Handler) {
        self.port = NWEndpoint.Port(rawValue: port) ?? 8765
        self.handler = handler
    }

    func start(serviceName: String, txt: [String: String]) throws {
        let parameters = NWParameters.tcp
        parameters.acceptLocalOnly = true
        let listener = try NWListener(using: parameters, on: port)
        let record = NWTXTRecord(txt)
        listener.service = NWListener.Service(name: serviceName, type: "_macbridge._tcp", domain: nil, txtRecord: record)
        listener.newConnectionHandler = { [weak self] conn in self?.accept(conn) }
        listener.stateUpdateHandler = { state in NSLog("MacBridge listener: \(state)") }
        listener.start(queue: queue)
        self.listener = listener
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    private func accept(_ connection: NWConnection) {
        guard let host = Self.localHost(of: connection.endpoint) else {
            NSLog("MacBridge: rejected non-local connection from \(connection.endpoint)")
            connection.cancel()
            return
        }
        countLock.lock()
        let allowed = activeConnections < Self.maxConnections
        if allowed { activeConnections += 1 }
        countLock.unlock()
        guard allowed else { connection.cancel(); return }

        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .cancelled, .failed:
                guard let self else { return }
                self.countLock.lock(); self.activeConnections -= 1; self.countLock.unlock()
                connection.stateUpdateHandler = nil
            default: break
            }
        }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + Self.requestTimeout) { connection.cancel() }
        receive(connection, host: host, buffer: Data())
    }

    private func receive(_ connection: NWConnection, host: String, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16384) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            var buffer = buffer
            if let data { buffer.append(data) }

            switch Self.parse(buffer, host: host) {
            case .complete(let request):
                self.send(self.handler(request), on: connection)
            case .invalid(let status):
                self.send(.error("bad request", status: status), on: connection)
            case .incomplete where isComplete || error != nil:
                connection.cancel()
            case .incomplete:
                self.receive(connection, host: host, buffer: buffer)
            }
        }
    }

    private enum ParseResult {
        case complete(HTTPRequest)
        case incomplete
        case invalid(Int)
    }

    /// Returns a request once headers and the full body have arrived.
    private static func parse(_ data: Data, host: String) -> ParseResult {
        guard let headerEnd = data.range(of: Data("\r\n\r\n".utf8)) else {
            return data.count > maxHeaderBytes ? .invalid(431) : .incomplete
        }
        guard headerEnd.lowerBound <= maxHeaderBytes,
              let head = String(data: data[..<headerEnd.lowerBound], encoding: .utf8)
        else { return .invalid(431) }

        var lines = head.components(separatedBy: "\r\n")
        let requestLine = lines.removeFirst().split(separator: " ")
        guard requestLine.count == 3, ["GET", "POST"].contains(requestLine[0]),
              requestLine[1].hasPrefix("/")
        else { return .invalid(400) }

        var headers: [String: String] = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { return .invalid(400) }
            let key = line[..<colon].lowercased()
            guard headers[key] == nil else { return .invalid(400) } // no duplicate headers
            headers[key] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        if headers["transfer-encoding"] != nil { return .invalid(400) }

        let lengthText = headers["content-length"] ?? "0"
        guard lengthText.allSatisfy(\.isASCII), lengthText.allSatisfy(\.isNumber),
              let length = Int(lengthText), length <= maxBodyBytes
        else { return .invalid(413) }

        let body = data[headerEnd.upperBound...]
        guard body.count >= length else { return .incomplete }

        let target = String(requestLine[1])
        let path = target.components(separatedBy: "?").first ?? "/"
        return .complete(HTTPRequest(
            method: String(requestLine[0]), target: target, path: path, headers: headers,
            body: Data(body.prefix(length)), remoteHost: host
        ))
    }

    private func send(_ response: HTTPResponse, on connection: NWConnection) {
        let reason: String
        switch response.status {
        case 200: reason = "OK"
        case 400: reason = "Bad Request"
        case 401: reason = "Unauthorized"
        case 403: reason = "Forbidden"
        case 404: reason = "Not Found"
        case 413: reason = "Payload Too Large"
        case 429: reason = "Too Many Requests"
        case 431: reason = "Request Header Fields Too Large"
        default: reason = "Error"
        }
        var head = "HTTP/1.1 \(response.status) \(reason)\r\n"
        head += "Content-Type: application/json\r\n"
        head += "Content-Length: \(response.body.count)\r\n"
        head += "Cache-Control: no-store\r\n"
        for (key, value) in response.headers.sorted(by: { $0.key < $1.key }) {
            head += "\(key): \(value)\r\n"
        }
        head += "Connection: close\r\n\r\n"
        var out = Data(head.utf8)
        out.append(response.body)
        connection.send(content: out, completion: .contentProcessed { _ in connection.cancel() })
    }

    // MARK: Local network check

    /// The remote IP if it is on the local network, otherwise nil.
    static func localHost(of endpoint: NWEndpoint) -> String? {
        guard case let .hostPort(host, _) = endpoint else { return nil }
        switch host {
        case .ipv4(let address):
            return isLocal(ipv4: [UInt8](address.rawValue)) ? "\(address)" : nil
        case .ipv6(let address):
            let bytes = [UInt8](address.rawValue)
            guard bytes.count == 16 else { return nil }
            if bytes[0..<10].allSatisfy({ $0 == 0 }) && bytes[10] == 0xff && bytes[11] == 0xff {
                return isLocal(ipv4: Array(bytes[12..<16])) ? "\(address)" : nil // IPv4-mapped
            }
            let loopback = bytes[0..<15].allSatisfy { $0 == 0 } && bytes[15] == 1
            let linkLocal = bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80 // fe80::/10
            let uniqueLocal = (bytes[0] & 0xfe) == 0xfc // fc00::/7
            return loopback || linkLocal || uniqueLocal ? "\(address)" : nil
        default:
            return nil
        }
    }

    private static func isLocal(ipv4 b: [UInt8]) -> Bool {
        guard b.count == 4 else { return false }
        return b[0] == 10 // 10.0.0.0/8
            || (b[0] == 172 && (16...31).contains(b[1])) // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168) // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254) // link-local
            || b[0] == 127 // loopback
    }
}
