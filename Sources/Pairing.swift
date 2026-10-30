import AppKit
import CoreImage
import Network
import SystemConfiguration

/// Links an iPhone without Terminal. The QR code carries this Mac's addresses and a one-time code; the phone sends its
/// SSH key back over the local network, and the key goes into ~/.ssh/authorized_keys allowed to run only ClipBubble's relay.
final class PairingServer {
    private var expires = Date.distantPast
    private let code = (0..<16).map { _ in String(format: "%02x", UInt8.random(in: 0...255)) }.joined()
    private let queue = DispatchQueue(label: "clipbubble.pairing")
    private var listener: NWListener?
    /// Main thread: the link to put in the QR code, once the listener has a port.
    var onReady: (URL) -> Void = { _ in }
    /// Main thread: a phone was added, with its name.
    var onPaired: (String) -> Void = { _ in }

    func start() {
        guard let l = try? NWListener(using: .tcp, on: .any) else { return }
        listener = l
        expires = Date().addingTimeInterval(600)
        l.newConnectionHandler = { [weak self] c in self?.serve(c) }
        l.stateUpdateHandler = { [weak self] state in
            guard case .ready = state, let self, let port = l.port?.rawValue else { return }
            let url = self.link(port: port)
            DispatchQueue.main.async { self.onReady(url) }
        }
        l.start(queue: queue)
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    private func link(port: UInt16) -> URL {
        var c = URLComponents()
        c.scheme = "clipbubble"
        c.host = "pair"
        c.queryItems = [.init(name: "u", value: NSUserName()), .init(name: "h", value: Self.addresses().joined(separator: ",")),
                        .init(name: "p", value: String(port)), .init(name: "c", value: code),
                        .init(name: "n", value: SyncEngine.shared.name)]
        return c.url!
    }

    /// The .local name first, since it survives a new IP address; then every non-loopback IPv4 address.
    static func addresses() -> [String] {
        var out: [String] = []
        if let name = SCDynamicStoreCopyLocalHostName(nil) as String? { out.append(name + ".local") }
        var ifs: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifs) == 0 else { return out }
        defer { freeifaddrs(ifs) }
        var p = ifs
        while let i = p?.pointee {
            defer { p = i.ifa_next }
            guard let sa = i.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET), i.ifa_flags & UInt32(IFF_LOOPBACK) == 0,
                  i.ifa_flags & UInt32(IFF_UP) != 0 else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 else { continue }
            let ip = String(cString: host)
            if !ip.hasPrefix("169.254."), !out.contains(ip) { out.append(ip) }
        }
        return out
    }

    /// One request line of JSON in, one line out.
    private func serve(_ c: NWConnection) {
        c.start(queue: queue)
        var buf = Data()
        func read() {
            c.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] d, _, done, err in
                guard let self else { return c.cancel() }
                if let d { buf.append(d) }
                if let nl = buf.firstIndex(of: 10) { return self.answer(Data(buf[..<nl]), on: c) }
                if done || err != nil || buf.count > 8192 { return c.cancel() }
                read()
            }
        }
        read()
    }

    private func answer(_ line: Data, on c: NWConnection) {
        var reply: [String: Any] = ["ok": false]
        if let req = try? JSONSerialization.jsonObject(with: line) as? [String: String],
           req["code"] == code, Date() < expires,
           let key = req["key"], let name = req["name"] {
            do {
                try AuthorizedKeys.add(key: key, name: name)
                reply = ["ok": true, "user": NSUserName(), "name": SyncEngine.shared.name, "hostKey": Self.hostKey() ?? ""]
                SyncLog.write("paired with \(name)")
                stop()
                DispatchQueue.main.async { self.onPaired(name) }
            } catch {
                reply["error"] = "\(error)"
            }
        }
        var out = (try? JSONSerialization.data(withJSONObject: reply)) ?? Data()
        out.append(10)
        c.send(content: out, completion: .contentProcessed { _ in c.cancel() })
    }

    /// The key sshd shows clients, so the phone can check it's talking to this Mac from the first connection.
    static func hostKey() -> String? {
        guard let s = try? String(contentsOfFile: "/etc/ssh/ssh_host_ed25519_key.pub", encoding: .utf8) else { return nil }
        return s.split(separator: " ").prefix(2).joined(separator: " ")
    }

    static func qr(_ url: URL) -> NSImage? {
        guard let f = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        f.setValue(Data(url.absoluteString.utf8), forKey: "inputMessage")
        f.setValue("M", forKey: "inputCorrectionLevel")
        guard let img = f.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)) else { return nil }
        let rep = NSCIImageRep(ciImage: img)
        let ns = NSImage(size: rep.size)
        ns.addRepresentation(rep)
        return ns
    }

    /// Remote Login is what the phone's SSH connection reaches.
    static func remoteLoginOn() -> Bool {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        var a = sockaddr_in()
        a.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        a.sin_family = sa_family_t(AF_INET)
        a.sin_port = in_port_t(22).bigEndian
        a.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } } == 0
    }
}

/// ~/.ssh/authorized_keys, which holds keys that aren't ClipBubble's. Every line it doesn't own stays byte for byte:
/// one invalid byte or a CRLF must never make the file read as empty, or the rewrite would drop the owner's keys.
enum AuthorizedKeys {
    enum Failure: Error { case badKey }

    static var file: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh/authorized_keys") }

    /// Adds the phone's key, limited to running this app's relay: no shell, no forwarding, no terminal.
    static func add(key: String, name: String, to file: URL = AuthorizedKeys.file) throws {
        let parts = key.split(separator: " ")
        guard parts.count == 2, parts[0] == "ssh-ed25519", parts[1].allSatisfy({ $0.isLetter || $0.isNumber || "+/=".contains($0) }),
              let exe = Bundle.main.executablePath, !exe.contains("'") else { throw Failure.badKey }
        let label = name.filter { $0.isLetter || $0.isNumber || $0 == " " || $0 == "-" }.prefix(40)
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        // Pairing the same phone again replaces its line, so a moved app gets a working path.
        var kept = try lines(of: file).filter { $0.range(of: Data(parts[1].utf8)) == nil }
        while kept.last?.isEmpty == true { kept.removeLast() }
        kept.append(Data("restrict,command=\"'\(exe)' --peer\" \(key) ClipBubble on \(label)".utf8))
        try write(kept, to: file)
    }

    private static func lines(of file: URL) throws -> [Data] {
        guard FileManager.default.fileExists(atPath: file.path) else { return [] }
        return try Data(contentsOf: file).split(separator: 10, omittingEmptySubsequences: false).map { Data($0) }
    }

    private static func write(_ lines: [Data], to file: URL) throws {
        // Written through a symlink, so a key file kept in dotfiles stays linked.
        let target = file.resolvingSymlinksInPath()
        try Data(lines.joined(separator: [10]) + [10]).write(to: target, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: target.path)
    }
}
