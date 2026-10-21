import Foundation
import Network

/// The phone's half of pairing (the computer's is Sources/Pairing.swift, Linux/clipbubble/pairing.py or
/// Windows/Sources/Pairing.cs): a `clipbubble://pair` link from the computer's QR code says where it is and carries a
/// one-time code. The phone sends its key; the computer answers with its user and host key.
enum Pairing {
    enum Failure: LocalizedError {
        case badLink, unreachable, refused(String)
        var errorDescription: String? {
            switch self {
            case .badLink: return "That isn't a ClipBubble pairing code."
            case .unreachable: return "Couldn't reach the computer. Is this iPhone on the same Wi-Fi?"
            case .refused(let why): return why
            }
        }
    }

    /// Returns the new link's alias (`user@host`) and the computer's name.
    static func pair(_ url: URL) async throws -> (alias: String, name: String) {
        guard url.scheme == "clipbubble", url.host == "pair",
              let q = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let hosts = q.first(where: { $0.name == "h" })?.value?.split(separator: ",").map(String.init),
              let port = q.first(where: { $0.name == "p" })?.value.flatMap(UInt16.init),
              let code = q.first(where: { $0.name == "c" })?.value else { throw Failure.badLink }
        var req = try JSONSerialization.data(withJSONObject: ["code": code, "key": Keys.publicKey, "name": Platform.deviceName])
        req.append(10)
        // Every address at once, so one a firewall drops costs nothing. The first (the .local name, which survives a
        // new IP address) gets a head start, since all of them reach the same computer.
        let replies = await withTaskGroup(of: (String, [String: Any]?).self) { group in
            for (i, host) in hosts.enumerated() {
                group.addTask {
                    try? await Task.sleep(nanoseconds: UInt64(i) * 300_000_000)
                    guard !Task.isCancelled, let d = await exchange(host: host, port: port, send: req) else { return (host, nil) }
                    return (host, try? JSONSerialization.jsonObject(with: d) as? [String: Any])
                }
            }
            var out: [(String, [String: Any])] = []
            for await (host, r) in group {
                guard let r else { continue }
                out.append((host, r))
                if r["ok"] as? Bool == true { group.cancelAll(); break }
            }
            return out
        }
        guard let (host, r) = replies.first(where: { $0.1["ok"] as? Bool == true }), let user = r["user"] as? String else {
            guard let no = replies.first?.1 else { throw Failure.unreachable }
            throw Failure.refused(no["error"] as? String ?? "The computer said no. Show a new code there and scan it again.")
        }
        // Trusts exactly the key the computer reported, so even the first SSH connection is checked.
        if let key = r["hostKey"] as? String, !key.isEmpty { Platform.defaults.set(key, forKey: "sync.hostkey.\(host):22") }
        return ("\(user)@\(host)", r["name"] as? String ?? host)
    }

    /// One line out, one line back, or nil if the host can't be reached within a few seconds. Once connected it waits
    /// up to two minutes for the answer: Windows asks its user before adding the key.
    private static func exchange(host: String, port: UInt16, send: Data) async -> Data? {
        let c = NWConnection(host: .init(host), port: .init(rawValue: port)!, using: .tcp)
        return await withTaskCancellationHandler { await withCheckedContinuation { cont in
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
            q.asyncAfter(deadline: .now() + 4) { if case .ready = c.state {} else { finish(nil) } }
            q.asyncAfter(deadline: .now() + 120) { finish(nil) }
        } } onCancel: { c.cancel() }
    }
}
