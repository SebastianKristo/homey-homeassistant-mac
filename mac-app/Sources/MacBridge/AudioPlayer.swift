import AVFoundation
import Foundation

/// Plays a short audio clip (text-to-speech from Home Assistant). Only fetches from the same
/// computer that sent the signed request, never follows redirects, and caps size and time.
final class AudioPlayer: NSObject, URLSessionTaskDelegate {
    static let maxBytes = 20 * 1024 * 1024
    private var player: AVAudioPlayer?
    private lazy var session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)

    enum Failure: Error { case notAllowed, download, decode }

    /// `requester` is the IP of the client whose signed request asked for this.
    func play(_ string: String, requester: String, volume: Float?) throws {
        guard let url = URL(string: string), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", let host = url.host,
              Self.resolves(host, to: requester)
        else { throw Failure.notAllowed }

        var request = URLRequest(url: url, timeoutInterval: 20)
        request.httpMethod = "GET"
        let done = DispatchSemaphore(value: 0)
        var result: Result<Data, Failure> = .failure(.download)
        session.dataTask(with: request) { data, response, _ in
            if let data, let http = response as? HTTPURLResponse, http.statusCode == 200, data.count <= Self.maxBytes {
                result = .success(data)
            }
            done.signal()
        }.resume()
        guard done.wait(timeout: .now() + 25) == .success else { throw Failure.download }
        let data = try result.get()

        guard let player = try? AVAudioPlayer(data: data) else { throw Failure.decode }
        if let volume { player.volume = max(0, min(1, volume)) }
        DispatchQueue.main.async {
            self.player?.stop()
            self.player = player
            player.play()
        }
    }

    func stop() {
        DispatchQueue.main.async { self.player?.stop() }
    }

    // Never follow redirects (they could point anywhere).
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    /// True if `host` (a name or IP) resolves to the requester's IP address.
    static func resolves(_ host: String, to requester: String) -> Bool {
        let want = normalize(requester)
        var hints = addrinfo(ai_flags: 0, ai_family: AF_UNSPEC, ai_socktype: SOCK_STREAM, ai_protocol: 0,
                             ai_addrlen: 0, ai_canonname: nil, ai_addr: nil, ai_next: nil)
        var list: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &list) == 0, let first = list else { return false }
        defer { freeaddrinfo(list) }
        for info in sequence(first: first, next: { $0.pointee.ai_next }) {
            guard let addr = info.pointee.ai_addr else { continue }
            var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, info.pointee.ai_addrlen, &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST) == 0,
               normalize(String(cString: buffer)) == want {
                return true
            }
        }
        return false
    }

    private static func normalize(_ ip: String) -> String {
        var s = ip.lowercased()
        if let percent = s.firstIndex(of: "%") { s = String(s[..<percent]) }
        if s.hasPrefix("::ffff:") { s = String(s.dropFirst(7)) }
        return s
    }
}
