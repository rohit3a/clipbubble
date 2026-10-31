import AppKit

/// What the shared sync engine needs from macOS.
enum Platform {
    static let os = "macos"
    static var deviceName: String { Host.current().localizedName ?? "Mac" }
    static let dataDir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("ClipBubble")
    static let defaults = UserDefaults.standard
    static let retryCap: Double = 300
    static let hostChanged = "Connect once in Terminal to trust this host"
    static let keyRefused = "Needs SSH key login"
    static var downloads: URL {
        FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads")
    }

    static func apply(_ c: Clip) {
        DispatchQueue.main.async { if Board.write(c) { Capture.ignoreChange(NSPasteboard.general.changeCount, hash: c.hash) } }
    }

    static func listen(_ engine: SyncEngine) -> AnyObject? { PeerServer(engine: engine) }

    static func onWake(_ f: @escaping () -> Void) {
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: nil) { _ in f() }
    }

    /// Runs `command` on `alias` with /usr/bin/ssh, so ~/.ssh/config, keys and the agent all apply.
    static func dial(_ alias: String, _ command: String, stderr: @escaping (Data) -> Void, exited: @escaping () -> Void) throws -> Tunnel {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
        p.arguments = ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=15",
                       "-o", "ServerAliveCountMax=2", "--", alias, command]
        let inPipe = Pipe(), outPipe = Pipe(), errPipe = Pipe()
        p.standardInput = inPipe
        p.standardOutput = outPipe
        p.standardError = errPipe
        errPipe.fileHandleForReading.readabilityHandler = { h in
            let d = h.availableData
            if d.isEmpty { h.readabilityHandler = nil } else { stderr(d) }
        }
        p.terminationHandler = { _ in exited() }
        try p.run()
        return ProcessTunnel(process: p, input: outPipe.fileHandleForReading, output: inPipe.fileHandleForWriting)
    }
}

final class ProcessTunnel: Tunnel {
    let input: FileHandle
    let output: FileHandle
    private let process: Process

    init(process: Process, input: FileHandle, output: FileHandle) {
        self.process = process
        self.input = input
        self.output = output
    }

    func close() { process.terminate() }
}

// MARK: - Local endpoint

/// Where `--peer` finds the running app. Short enough for a Unix socket path.
var peerSocketPath: String {
    let p = Paths.dir.appendingPathComponent("peer.sock").path
    return p.utf8.count < 100 ? p : "/tmp/clipbubble-\(getuid()).sock"
}

/// The app's Unix socket. Each connection is a peer that came in over SSH through `--peer`.
final class PeerServer {
    private var source: DispatchSourceRead?

    init?(engine: SyncEngine) {
        let path = peerSocketPath
        unlink(path)
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0, bindUnix(fd, path), listen(fd, 8) == 0 else { if fd >= 0 { close(fd) }; return nil }
        chmod(path, 0o600)
        let src = DispatchSource.makeReadSource(fileDescriptor: fd, queue: engine.queue)
        src.setEventHandler {
            let c = accept(fd, nil, nil)
            guard c >= 0 else { return }
            var on: Int32 = 1
            setsockopt(c, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
            let h = FileHandle(fileDescriptor: c, closeOnDealloc: true)
            engine.begin(Session(input: h, output: h, engine: engine))
        }
        src.setCancelHandler { close(fd) }
        src.resume()
        source = src
    }
}

private func bindUnix(_ fd: Int32, _ path: String) -> Bool {
    var addr = sockaddr_un()
    addr.sun_family = sa_family_t(AF_UNIX)
    let bytes = Array(path.utf8)
    guard bytes.count < MemoryLayout.size(ofValue: addr.sun_path) else { return false }
    withUnsafeMutableBytes(of: &addr.sun_path) { buf in
        for (i, b) in bytes.enumerated() { buf[i] = b }
        buf[bytes.count] = 0
    }
    return withUnsafePointer(to: &addr) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } == 0
    }
}

private func connectUnix(_ path: String) -> Int32 {
    var addr = sockaddr_un()
    addr.sun_family = sa_family_t(AF_UNIX)
    let bytes = Array(path.utf8)
    guard bytes.count < MemoryLayout.size(ofValue: addr.sun_path) else { return -1 }
    withUnsafeMutableBytes(of: &addr.sun_path) { buf in
        for (i, b) in bytes.enumerated() { buf[i] = b }
        buf[bytes.count] = 0
    }
    let fd = socket(AF_UNIX, SOCK_STREAM, 0)
    guard fd >= 0 else { return -1 }
    let ok = withUnsafePointer(to: &addr) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } == 0
    }
    if !ok { close(fd); return -1 }
    var on: Int32 = 1
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
    return fd
}

/// `ClipBubble --peer`, run by ssh on this Mac: pipes ssh's stdio to the running app, starting it if needed.
enum PeerRelay {
    static func run() -> Never {
        signal(SIGPIPE, SIG_IGN)
        var fd = connectUnix(peerSocketPath)
        if fd < 0 {
            let open = Process()
            open.executableURL = URL(fileURLWithPath: "/usr/bin/open")
            // Handing it a URL, which it ignores, because `open` on the app alone reopens a running one, and that shows the panel.
            open.arguments = ["-gj", "-a", Bundle.main.bundlePath, "clipbubble:peer"]
            try? open.run()
            for _ in 0..<40 where fd < 0 {
                usleep(250_000)
                fd = connectUnix(peerSocketPath)
            }
        }
        guard fd >= 0 else {
            FileHandle.standardError.write(Data("CLIPBUBBLE_NOT_RUNNING\n".utf8))
            exit(3)
        }
        Thread.detachNewThread { pipeBytes(from: 0, to: fd); shutdown(fd, SHUT_WR) }
        pipeBytes(from: fd, to: 1)
        exit(0)
    }

    private static func pipeBytes(from src: Int32, to dst: Int32) {
        var buf = [UInt8](repeating: 0, count: 65536)
        while true {
            let n = read(src, &buf, buf.count)
            if n <= 0 { return }
            var off = 0
            while off < n {
                let w = buf[off..<n].withUnsafeBytes { write(dst, $0.baseAddress, n - off) }
                if w <= 0 { return }
                off += w
            }
        }
    }
}

// MARK: - SSH config

enum SSHConfig {
    /// Host aliases from ~/.ssh/config and the files it includes, skipping patterns.
    static func hosts() -> [String] {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh")
        var seen: [String] = []
        var visited: Set<String> = []
        func read(_ path: String) {
            guard visited.insert(path).inserted, let text = try? String(contentsOfFile: path, encoding: .utf8) else { return }
            for line in text.split(whereSeparator: \.isNewline) {
                let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "=" }).map(String.init)
                guard let key = parts.first?.lowercased() else { continue }
                if key == "host" {
                    for h in parts.dropFirst() where !h.contains("*") && !h.contains("?") && !h.hasPrefix("!") && !seen.contains(h) {
                        seen.append(h)
                    }
                } else if key == "include" {
                    for pattern in parts.dropFirst() {
                        let p = (pattern as NSString).expandingTildeInPath
                        let full = p.hasPrefix("/") ? p : dir.appendingPathComponent(p).path
                        for f in glob(full) { read(f) }
                    }
                }
            }
        }
        read(dir.appendingPathComponent("config").path)
        return seen
    }

    private static func glob(_ pattern: String) -> [String] {
        var g = glob_t()
        defer { globfree(&g) }
        guard Darwin.glob(pattern, 0, nil, &g) == 0 else { return [] }
        return (0..<Int(g.gl_pathc)).compactMap { g.gl_pathv[$0].map { String(cString: $0) } }
    }
}
