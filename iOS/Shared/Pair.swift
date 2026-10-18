import Foundation
import Network

/// The phone's half of pairing (the Mac's is Sources/Pairing.swift): a `clipbubble://pair` link from the Mac's QR code
/// says where the Mac is and carries a one-time code. The phone sends its key; the Mac answers with its user and host key.
enum Pairing {
    enum Failure: LocalizedError {
        case badLink, unreachable, refused(String)
        var errorDescription: String? {
            switch self {
            case .badLink: return "That isn't a ClipBubble pairing code."
            case .unreachable: return "Couldn't reach the Mac. Is this iPhone on the same Wi-Fi?"
            case .refused(let why): return why
            }
        }
    }

    /// Returns the new link's alias (`user@host`) and the Mac's name.
    static func pair(_ url: URL) async throws -> (alias: String, name: String) {
        guard url.scheme == "clipbubble", url.host == "pair",
              let q = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let hosts = q.first(where: { $0.name == "h" })?.value?.split(separator: ",").map(String.init),
              let port = q.first(where: { $0.name == "p" })?.value.flatMap(UInt16.init),
              let code = q.first(where: { $0.name == "c" })?.value else { throw Failure.badLink }
        var req = try JSONSerialization.data(withJSONObject: ["code": code, "key": Keys.publicKey, "name": Platform.deviceName])
        req.append(10)
        for host in hosts {
            guard let reply = await exchange(host: host, port: port, send: req) else { continue }
            guard let r = try? JSONSerialization.jsonObject(with: reply) as? [String: Any], r["ok"] as? Bool == true,
                  let user = r["user"] as? String else {
                throw Failure.refused("The Mac said no. Show a new code on the Mac and scan it again.")
            }
            // Trusts exactly the key the Mac reported, so even the first SSH connection is checked.
            if let key = r["hostKey"] as? String, !key.isEmpty { Platform.defaults.set(key, forKey: "sync.hostkey.\(host):22") }
            return ("\(user)@\(host)", r["name"] as? String ?? host)
        }
        throw Failure.unreachable
    }

    /// One line out, one line back, or nil if the host can't be reached within a few seconds.
    private static func exchange(host: String, port: UInt16, send: Data) async -> Data? {
        await withCheckedContinuation { cont in
            let c = NWConnection(host: .init(host), port: .init(rawValue: port)!, using: .tcp)
            let q = DispatchQueue(label: "clipbubble.pair")
            var done = false
            func finish(_ d: Data?) {
                guard !done else { return }
                done = true
                c.cancel()
                cont.resume(returning: d)
            }
            var buf = Data()
            func read() {
                c.receive(minimumIncompleteLength: 1, maximumLength: 4096) { d, _, end, err in
                    if let d { buf.append(d) }
                    if let nl = buf.firstIndex(of: 10) { return finish(Data(buf[..<nl])) }
                    if end || err != nil { return finish(nil) }
                    read()
                }
            }
            c.stateUpdateHandler = { s in
                switch s {
                case .ready: c.send(content: send, completion: .contentProcessed { _ in read() })
                case .failed, .cancelled: finish(nil)
                default: break
                }
            }
            c.start(queue: q)
            q.asyncAfter(deadline: .now() + 4) { finish(nil) }
        }
    }
}
