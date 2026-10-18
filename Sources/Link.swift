import Foundation
import Network

/// Links this Mac's history with ClipBubble on other machines over SSH (docs/sync-protocol.md).
/// Everything here runs on `queue`. Nothing connects until a host is switched on or a peer dials in.
final class SyncEngine {
    static let shared = SyncEngine()

    let queue = DispatchQueue(label: "clipbubble.sync", qos: .utility)
    let device: String
    let name: String
    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    /// Puts a synced item on the clipboard. Tests and `--peer-stdio` swap it so they never touch the real one.
    var apply: (Clip) -> Void = Platform.apply
    /// Main thread: an item arrived (for the bubble), or a link's state changed (for Settings).
    var onReceived: () -> Void = {}
    var onStatus: () -> Void = {}
    /// Main thread: files moving right now, about four times a second while any are.
    var onTransfers: ([String: Transfer]) -> Void = { _ in }

    private lazy var db = DB()
    private var sessions: [Session] = []
    private var links: [String: HostLink] = [:]
    private var server: AnyObject?
    private var pathMonitor: NWPathMonitor?
    private var hooked = false
    /// When each outgoing item was sent, to log copy-to-applied latency.
    private var sentAt: [String: Double] = [:]
    /// Items you pressed Get on: they download whatever their size, again after a dropped link.
    private var wanted: Set<String> = []
    private var progressTimer: DispatchSourceTimer?
    private var lastTransfers: [String: Transfer] = [:]

    private init() {
        let idFile = Paths.dir.appendingPathComponent("device-id")
        if let id = try? String(contentsOf: idFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines), !id.isEmpty {
            device = id
        } else {
            device = UUID().uuidString.lowercased()
            try? FileManager.default.createDirectory(at: Paths.dir, withIntermediateDirectories: true)
            try? device.write(to: idFile, atomically: true, encoding: .utf8)
        }
        name = ProcessInfo.processInfo.environment["CLIPBUBBLE_DEVICE_NAME"] ?? Platform.deviceName
    }

    // MARK: Hosts

    static var enabledHosts: [String] { UserDefaults.standard.stringArray(forKey: "sync.hosts") ?? [] }
    static var paused: Bool { UserDefaults.standard.bool(forKey: "sync.paused") }

    /// Called at launch: listens for peers (no CPU while idle) and dials the hosts switched on.
    func start() {
        queue.async { [self] in
            if server == nil { server = Platform.listen(self) }
            reconcileLinks()
        }
    }

    func setHost(_ alias: String, on: Bool) {
        var hosts = Self.enabledHosts.filter { $0 != alias }
        if on { hosts.append(alias) }
        UserDefaults.standard.set(hosts, forKey: "sync.hosts")
        queue.async { self.reconcileLinks() }
    }

    /// Tries a host again now, say right after setting it up.
    func redial(_ alias: String) { queue.async { self.links[alias]?.redial() } }

    // MARK: Manual transfers

    /// Get: downloads an item that's still on another device, whatever its size.
    func get(_ hash: String) {
        queue.async { [self] in
            wanted.insert(hash)
            guard let s = sessions.first(where: { $0.offered[hash] != nil }), let e = s.offered[hash] else { return }
            s.download(makeDownload(e, from: s, apply: false))
            watchTransfers()
        }
    }

    /// Send to: asks a linked device to fetch one of this machine's items now.
    func send(_ hash: String, toDevice device: String) {
        queue.async { [self] in
            guard let s = sessions.first(where: { $0.peer?.device == device && $0.summarized }) else { return }
            if let st = db.stamp(hash: hash), st.beats(s.known[hash]) { send(st, to: s) }
            s.send(.offer(hash: hash))
        }
    }

    /// Stops a transfer either way and throws away what arrived.
    func cancelTransfer(_ hash: String) {
        queue.async { [self] in
            wanted.remove(hash)
            for s in sessions where s.isTransferring(hash) {
                s.cancelDownload(hash)
                s.cancelUpload(hash)
                s.send(.cancel(hash: hash))
            }
            publishTransfers()
        }
    }


    private func makeDownload(_ e: Entry, from s: Session, apply: Bool) -> Download {
        Download(hash: e.hash, kind: e.kind, names: e.blobs.map(\.name), shas: e.blobs.map(\.sha), sizes: e.blobs.map(\.size),
                 from: s.peer?.name ?? "Device", apply: apply)
    }

    /// Progress ticks only while something is moving, so an idle link costs nothing.
    func watchTransfers() {
        guard progressTimer == nil else { return publishTransfers() }
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: 0.25)
        t.setEventHandler { [weak self] in self?.publishTransfers() }
        progressTimer = t
        t.resume()
    }

    private func publishTransfers() {
        var all: [String: Transfer] = [:]
        for s in sessions { all.merge(s.transfers()) { a, _ in a } }
        if all.isEmpty { progressTimer?.cancel(); progressTimer = nil }
        guard all != lastTransfers else { return }
        lastTransfers = all
        DispatchQueue.main.async { self.onTransfers(all) }
    }

    func setPaused(_ paused: Bool) {
        UserDefaults.standard.set(paused, forKey: "sync.paused")
        queue.async { [self] in
            if paused { sessions.forEach { $0.close() } }
            reconcileLinks()
        }
    }

    /// Statuses for Settings, read on the main thread.
    private(set) var statuses: [String: LinkState] = [:]

    private func reconcileLinks() {
        let want = Self.paused ? [] : Set(Self.enabledHosts)
        for (alias, link) in links where !want.contains(alias) {
            link.stop()
            links[alias] = nil
        }
        for alias in want where links[alias] == nil {
            let link = HostLink(alias: alias, engine: self)
            links[alias] = link
            link.connect()
        }
        if !links.isEmpty { watchNetwork() }
        publishStatus()
    }

    /// Devices linked right now, for Send to. Read on the main thread.
    private(set) var linked: [Hello] = []

    func publishStatus() {
        var s: [String: LinkState] = [:]
        for (alias, link) in links { s[alias] = link.state }
        let peers = sessions.compactMap(\.peer)
        DispatchQueue.main.async { self.statuses = s; self.linked = peers; self.onStatus() }
    }

    /// Wake and network changes redial right away instead of waiting out the backoff.
    private func watchNetwork() {
        guard pathMonitor == nil else { return }
        let m = NWPathMonitor()
        m.pathUpdateHandler = { [weak self] p in
            guard p.status == .satisfied else { return }
            self?.queue.async { self?.links.values.forEach { $0.redialIfDown() } }
        }
        m.start(queue: queue)
        pathMonitor = m
        Platform.onWake { [weak self] in self?.queue.async { self?.links.values.forEach { $0.redial() } } }
    }

    // MARK: Sessions

    /// A peer dialled in through `--peer`, or a host link's ssh came up.
    func begin(_ s: Session) {
        if Self.paused && s.link == nil { s.close(); return }
        sessions.append(s)
        hookChanges()
        s.start(hello: Hello(proto: Sync.proto, device: device, name: name, os: Platform.os, version: version,
                             clock: Date().timeIntervalSince1970), summary: db.stamps(includingWaiting: false))
    }

    func ended(_ s: Session) {
        sessions.removeAll { $0 === s }
        s.link?.sessionEnded(s)
        publishStatus()
    }

    /// The change hook is installed once, the first time a link opens, and only queues work after that.
    private func hookChanges() {
        guard !hooked else { return }
        hooked = true
        DB.changed = { [weak self] hashes in self?.queue.async { self?.broadcast(hashes) } }
    }

    /// Sends local changes, and relays changes from one peer, to every peer that doesn't have them yet.
    private func broadcast(_ hashes: [String]) {
        let ready = sessions.filter(\.summarized)
        guard !ready.isEmpty else { return }
        for hash in Set(hashes) {
            guard let st = db.stamp(hash: hash) else { continue }
            for s in ready where st.beats(s.known[hash]) { send(st, to: s) }
        }
    }

    private func send(_ st: Stamp, to s: Session) {
        if st.deleted {
            s.known[st.hash] = st
            s.send(.tombstone(st))
            return
        }
        guard let c = db.clip(hash: st.hash), db.allowed(kind: c.kind, source: c.source), let e = entry(c) else { return }
        s.known[st.hash] = st
        // Only fresh copies get acked, so only they are timed.
        if c.origin.isEmpty, Date().timeIntervalSince(c.created) < 10 { sentAt[st.hash] = Date().timeIntervalSince1970 }
        s.send(.put(e))
    }

    private func entry(_ c: Clip) -> Entry? {
        var e = Entry(hash: c.hash, time: c.created.timeIntervalSince1970, kind: c.kind, text: "", source: c.source,
                      origin: c.origin.isEmpty ? device : c.origin, blobs: [])
        switch c.kind {
        case "text":
            e.text = c.text
        case "image":
            guard !c.remote, let f = c.file, let size = fileSize(f) else { return c.remote ? e : nil }
            e.blobs = [Blob(name: String(c.hash.dropFirst(2)) + ".png", size: size, sha: String(c.hash.dropFirst(2)))]
        default:
            if c.remote { e.text = c.text; return e }
            let files = blobFiles(c)
            e.text = c.paths.map { ($0 as NSString).lastPathComponent }.joined(separator: "\n")
            e.blobs = files.compactMap { p in fileSize(p).map { Blob(name: (p as NSString).lastPathComponent, size: $0, sha: "") } }
        }
        return e
    }

    /// The files an item can send: regular files only, in order. Folders show by name but don't transfer.
    private func blobFiles(_ c: Clip) -> [String] {
        if c.kind == "image" { return c.file.map { [$0] } ?? [] }
        return c.paths.filter { var dir: ObjCBool = false; return FileManager.default.fileExists(atPath: $0, isDirectory: &dir) && !dir.boolValue }
    }

    private func fileSize(_ p: String) -> Int64? {
        (try? FileManager.default.attributesOfItem(atPath: p)[.size] as? NSNumber)?.int64Value
    }

    // MARK: Frames

    func handle(_ f: Frame, from s: Session) {
        switch f {
        case .hello(let h):
            guard h.proto == Sync.proto else {
                SyncLog.write("\(h.name) speaks protocol \(h.proto), this device \(Sync.proto)")
                s.link?.versionMismatch(h)
                s.close()
                return
            }
            guard h.device != device, !sessions.contains(where: { $0 !== s && $0.peer?.device == h.device }) else {
                s.close()
                return
            }
            s.peer = h
            s.offset = h.clock - Date().timeIntervalSince1970
            if abs(s.offset) > 30 { SyncLog.write("\(h.name)'s clock is \(Int(s.offset)) s off") }
            db.saveDevice(id: h.device, name: h.name, os: h.os)
            s.link?.helloReceived(h)
            SyncLog.write("linked with \(h.name) (\(h.os) \(h.version))")
            publishStatus()
        case .summary(let stamps):
            guard s.peer != nil else { s.close(); return }
            s.known = Dictionary(stamps.map { ($0.hash, $0) }, uniquingKeysWith: { $0.beats($1) ? $0 : $1 })
            s.summarized = true
            let mine = reconcile(local: db.stamps(), remote: stamps)
            for st in mine.sorted(by: { $0.time < $1.time }) { send(st, to: s) }
            SyncLog.write("reconciled with \(s.peer!.name): sent \(mine.count) of \(stamps.count) known there")
        case .put(let e):
            received(e, from: s)
        case .tombstone(let st):
            guard st.deleted else { s.close(); return }
            if st.beats(s.known[st.hash]) { s.known[st.hash] = st }
            if merge(local: db.stamp(hash: st.hash), hasRow: db.clip(hash: st.hash) != nil, incoming: st) == .delete {
                s.cancelDownload(st.hash)
                db.tombstone(st)
                notifyUI()
            }
        case .fetch(let hash, let index, let offset):
            guard let c = db.clip(hash: hash), !c.remote, index < blobFiles(c).count,
                  s.upload(hash: hash, index: index, path: blobFiles(c)[index], offset: offset) else {
                s.send(.cancel(hash: hash))
                return
            }
            watchTransfers()
        case .chunk(let hash, let index, let offset, let total, let bytes):
            if let done = s.chunk(hash: hash, index: index, offset: offset, total: total, bytes: bytes) { finished(done, from: s) }
        case .cancel(let hash):
            s.cancelUpload(hash)
            s.cancelDownload(hash)
        case .applied(let hash):
            if let t = sentAt.removeValue(forKey: hash) {
                SyncLog.write("applied on \(s.peer?.name ?? "peer") in \(Int((Date().timeIntervalSince1970 - t) * 1000)) ms")
            }
        case .ping(let t):
            s.send(.pong(t))
        case .pong:
            break
        case .offer(let hash):
            guard let e = s.offered[hash], db.clip(hash: hash)?.remote == true else { return }
            s.download(makeDownload(e, from: s, apply: false))
            watchTransfers()
        }
    }

    private func received(_ e: Entry, from s: Session) {
        let st = e.stamp
        if st.beats(s.known[e.hash]) { s.known[e.hash] = st }
        guard e.time >= Date().timeIntervalSince1970 - DB.retention else { return }
        let row = db.clip(hash: e.hash)
        // Copied seconds ago on its device, and newer than anything here: it's what you just copied, so it goes on the clipboard.
        let fresh = Date().timeIntervalSince1970 - (e.time - s.offset) < 10 && e.time > (db.clips(limit: 1).first?.created.timeIntervalSince1970 ?? 0)
        switch merge(local: db.stamp(hash: e.hash), hasRow: row != nil, incoming: st) {
        case .delete:
            return
        case .ignore:
            guard row?.remote == true else { return }
        case .bump:
            db.bump(hash: e.hash, to: e.time)
            if fresh, let c = db.clip(hash: e.hash), !c.remote { applyNow(c, to: s) }
        case .insert:
            guard db.allowed(kind: e.kind, source: e.source) else { return }
            db.insert(e, origin: e.origin == device ? "" : e.origin)
            if e.blobs.isEmpty, fresh, let c = db.clip(hash: e.hash) { applyNow(c, to: s) }
        }
        // Still missing its bytes, whether new here or left over from a link that dropped mid-download.
        if !e.blobs.isEmpty, db.clip(hash: e.hash)?.remote == true {
            s.offered[e.hash] = e
            if e.size <= Sync.autoFetchLimit || wanted.contains(e.hash) {
                s.download(makeDownload(e, from: s, apply: fresh))
                watchTransfers()
            }
        }
        notifyUI()
    }

    private func applyNow(_ c: Clip, to s: Session) {
        apply(c)
        s.send(.applied(hash: c.hash))
    }

    private func finished(_ d: Download, from s: Session) {
        wanted.remove(d.hash)
        s.offered[d.hash] = nil
        if d.kind == "image" { db.received(hash: d.hash, file: d.paths.first, text: nil) }
        else { db.received(hash: d.hash, file: nil, text: d.paths.joined(separator: "\n")) }
        // Still the newest item once the bytes are here: put it on the clipboard.
        if d.apply, let c = db.clip(hash: d.hash), db.clips(limit: 1).first?.hash == d.hash { applyNow(c, to: s) }
        notifyUI()
    }

    private func notifyUI() { DispatchQueue.main.async { self.onReceived() } }

    /// `--peer-stdio`: one session on this process's stdin and stdout, with no UI. For tests and interop checks.
    func serveStdio() {
        queue.async { [self] in
            let s = Session(input: .standardInput, output: .standardOutput, engine: self)
            s.onClose = { exit(0) }
            begin(s)
        }
    }
}

// MARK: - Link state

enum LinkState: Equatable {
    case connecting
    case live(Hello)
    case needsKeyAuth
    case unknownHost
    case notInstalled
    case appNotRunning
    case versionMismatch(Hello)
    case offline(Date)

    var text: String {
        switch self {
        case .connecting: return "Connecting…"
        case .live(let h): return "Linked with \(h.name)"
        case .needsKeyAuth: return "Needs SSH key login"
        case .unknownHost: return "Connect once in Terminal to trust this host"
        case .notInstalled: return "ClipBubble isn't installed there"
        case .appNotRunning: return "ClipBubble isn't running there"
        case .versionMismatch(let h): return "Update needed (it has \(h.version))"
        case .offline(let at):
            let s = max(0, Int(at.timeIntervalSinceNow))
            return s > 0 ? "Offline, retrying in \(s < 60 ? "\(s) s" : "\(s / 60) min")" : "Offline"
        }
    }

    var ok: Bool { if case .live = self { true } else { false } }
}

/// A relay running on a host over SSH: frames in on `input`, frames out on `output`.
protocol Tunnel: AnyObject {
    var input: FileHandle { get }
    var output: FileHandle { get }
    func close()
}

/// One host: runs `--peer` there over SSH, retries with backoff, and turns SSH's errors into a status.
final class HostLink {
    let alias: String
    private unowned let engine: SyncEngine
    private var tunnel: Tunnel?
    private var session: Session?
    private var stderr = ""
    private var backoff: Double = 1
    private var retry: DispatchWorkItem?
    private var stopped = false
    private var mismatch: Hello?
    private(set) var state: LinkState = .connecting

    private var windows: Bool {
        get { UserDefaults.standard.string(forKey: "sync.os." + alias) == "windows" }
        set { UserDefaults.standard.set(newValue ? "windows" : "posix", forKey: "sync.os." + alias) }
    }

    init(alias: String, engine: SyncEngine) {
        self.alias = alias
        self.engine = engine
    }

    /// Finds ClipBubble where each platform installs it and runs its relay. Nothing from the clipboard goes in this string.
    private var remoteCommand: String {
        windows
            ? #""%LOCALAPPDATA%\Programs\ClipBubble\ClipBubble.exe" --peer"#
            : #"sh -c 'for p in "$HOME/.local/bin/clipbubble" "$HOME/Applications/ClipBubble.app/Contents/MacOS/ClipBubble" /Applications/ClipBubble.app/Contents/MacOS/ClipBubble; do [ -x "$p" ] && exec "$p" --peer; done; echo CLIPBUBBLE_NOT_INSTALLED >&2; exit 127'"#
    }

    func connect() {
        guard !stopped, tunnel == nil, !alias.hasPrefix("-") else { return }
        retry?.cancel()
        set(.connecting)
        stderr = ""
        let t: Tunnel
        do {
            t = try Platform.dial(alias, remoteCommand,
                stderr: { [weak self] d in self?.engine.queue.async { if (self?.stderr.count ?? 0) < 8192 { self?.stderr += String(decoding: d, as: UTF8.self) } } },
                exited: { [weak self] in self?.engine.queue.async { self?.exited() } })
        } catch { set(.offline(Date().addingTimeInterval(backoff))); scheduleRetry(); return }
        tunnel = t
        let s = Session(input: t.input, output: t.output, engine: engine)
        s.link = self
        session = s
        engine.begin(s)
    }

    func helloReceived(_ h: Hello) {
        backoff = 1
        mismatch = nil
        set(.live(h))
    }

    func versionMismatch(_ h: Hello) { mismatch = h }

    func sessionEnded(_ s: Session) {
        guard session === s else { return }
        session = nil
        tunnel?.close()
    }

    private func exited() {
        let wasLive = state.ok
        tunnel = nil
        if let s = session { session = nil; s.close() }
        guard !stopped else { return }
        let err = stderr
        if !windows && (err.contains("is not recognized") || err.contains("CommandNotFoundException")) {
            windows = true
            connect()
            return
        }
        let next: LinkState
        if let h = mismatch { next = .versionMismatch(h) }
        else if err.contains("CLIPBUBBLE_NOT_INSTALLED") || (windows && err.contains("cannot find the path")) { next = .notInstalled }
        else if err.contains("CLIPBUBBLE_NOT_RUNNING") { next = .appNotRunning }
        else if err.contains("Host key verification failed") { next = .unknownHost }
        else if err.contains("Permission denied") || err.contains("Too many authentication failures") { next = .needsKeyAuth }
        else { next = .offline(Date().addingTimeInterval(wasLive ? 1 : backoff)) }
        if !err.isEmpty { SyncLog.write("\(alias): \(err.trimmingCharacters(in: .whitespacesAndNewlines).suffix(300))") }
        // Problems only you can fix retry slowly; a dropped link comes back fast.
        if case .offline = next {} else { backoff = max(backoff, 60) }
        if wasLive { backoff = 1 }
        set(next)
        scheduleRetry()
    }

    private func scheduleRetry() {
        let w = DispatchWorkItem { [weak self] in self?.connect() }
        retry = w
        engine.queue.asyncAfter(deadline: .now() + backoff, execute: w)
        backoff = min(backoff * 2, 300)
    }

    func redialIfDown() { if case .offline = state { redial() } }

    func redial() {
        guard !stopped, tunnel == nil else { return }
        backoff = 1
        connect()
    }

    func stop() {
        stopped = true
        retry?.cancel()
        session?.close()
        tunnel?.close()
    }

    private func set(_ s: LinkState) {
        state = s
        engine.publishStatus()
    }
}

// MARK: - Session

/// One peer: reads frames, and writes them with control frames always ahead of file chunks.
final class Session {
    private let input: FileHandle
    private let output: FileHandle
    private unowned let engine: SyncEngine
    weak var link: HostLink?
    var onClose: () -> Void = {}
    var peer: Hello?
    var offset: Double = 0
    var summarized = false
    /// The latest event the peer is known to have per hash, so nothing is sent twice or echoed back.
    var known: [String: Stamp] = [:]
    /// Items the peer has bytes for that haven't come over yet, with their blob sizes, for Get and Send to.
    var offered: [String: Entry] = [:]
    private var reader = FrameReader()
    private var downloads: [String: Download] = [:]
    private var pendingDownloads: [Download] = []

    private let lock = NSLock()
    private var control: [Data] = []
    private var uploads: [Upload] = []
    private var writing = false
    private(set) var closed = false
    private let writeQueue = DispatchQueue(label: "clipbubble.sync.write", qos: .utility)

    init(input: FileHandle, output: FileHandle, engine: SyncEngine) {
        self.input = input
        self.output = output
        self.engine = engine
    }

    func start(hello: Hello, summary: [Stamp]) {
        input.readabilityHandler = { [weak self] h in
            let d = h.availableData
            guard let self else { return }
            self.engine.queue.async {
                guard !self.closed else { return }
                if d.isEmpty { self.close(); return }
                do { for f in try self.reader.feed(d) where !self.closed { self.engine.handle(f, from: self) } }
                catch { SyncLog.write("dropped \(self.peer?.name ?? "peer"): malformed frame"); self.close() }
            }
        }
        send(.hello(hello))
        send(.summary(summary))
    }

    func send(_ f: Frame) {
        lock.lock()
        control.append(f.encoded())
        lock.unlock()
        pump()
    }

    private func pump() {
        lock.lock()
        guard !writing, !closed else { lock.unlock(); return }
        writing = true
        lock.unlock()
        writeQueue.async { [self] in
            while let data = next() {
                do { try output.write(contentsOf: data) }
                catch { engine.queue.async { self.close() }; break }
            }
        }
    }

    /// The next bytes to write: any control frame first, else one chunk of the oldest upload.
    private func next() -> Data? {
        lock.lock()
        defer { lock.unlock() }
        if closed { writing = false; return nil }
        if !control.isEmpty { return control.removeFirst() }
        while let u = uploads.first {
            if let frame = u.nextChunk() {
                if u.done { uploads.removeFirst() }
                return frame.encoded()
            }
            uploads.removeFirst()
            control.append(Frame.cancel(hash: u.hash).encoded())
            return control.removeFirst()
        }
        writing = false
        return nil
    }

    func upload(hash: String, index: Int, path: String, offset: Int64) -> Bool {
        guard let u = Upload(hash: hash, index: index, path: path, offset: offset) else { return false }
        lock.lock()
        uploads.append(u)
        lock.unlock()
        pump()
        return true
    }

    func cancelUpload(_ hash: String) {
        lock.lock()
        uploads.removeAll { $0.hash == hash }
        lock.unlock()
    }

    func isTransferring(_ hash: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return downloads[hash] != nil || pendingDownloads.contains { $0.hash == hash } || uploads.contains { $0.hash == hash }
    }

    func transfers() -> [String: Transfer] {
        let name = peer?.name ?? "Device"
        var out: [String: Transfer] = [:]
        for d in downloads.values { out[d.hash] = Transfer(done: d.doneBytes, total: d.total, peer: name, sending: false) }
        for d in pendingDownloads { out[d.hash] = Transfer(done: d.doneBytes, total: d.total, peer: name, sending: false) }
        lock.lock()
        for u in uploads { out[u.hash] = Transfer(done: u.sent, total: u.total, peer: name, sending: true) }
        lock.unlock()
        return out
    }

    // Downloads run one at a time per peer, so a backlog can't flood the link.

    func download(_ d: Download) {
        guard downloads[d.hash] == nil, !pendingDownloads.contains(where: { $0.hash == d.hash }) else { return }
        if downloads.isEmpty { begin(d) } else { pendingDownloads.append(d) }
    }

    private func begin(_ d: Download) {
        downloads[d.hash] = d
        send(.fetch(hash: d.hash, index: d.index, offset: d.received))
    }

    func cancelDownload(_ hash: String) {
        pendingDownloads.removeAll { $0.hash == hash }
        guard let d = downloads.removeValue(forKey: hash) else { return }
        d.discard()
        startNext()
    }

    private func startNext() {
        guard downloads.isEmpty, !pendingDownloads.isEmpty else { return }
        begin(pendingDownloads.removeFirst())
    }

    /// Writes a chunk. Returns the download once its last file is in place.
    func chunk(hash: String, index: Int, offset: Int64, total: Int64, bytes: Data) -> Download? {
        guard let d = downloads[hash], index == d.index, offset == d.received else { return nil }
        switch d.write(bytes, total: total) {
        case .more:
            return nil
        case .failed:
            SyncLog.write("couldn't save \(d.names[safe: index] ?? hash) from \(d.from)")
            downloads[hash] = nil
            startNext()
            return nil
        case .nextFile:
            send(.fetch(hash: hash, index: d.index, offset: 0))
            return nil
        case .done:
            downloads[hash] = nil
            startNext()
            return d
        }
    }

    func close() {
        lock.lock()
        let was = closed
        closed = true
        uploads.removeAll()
        lock.unlock()
        guard !was else { return }
        input.readabilityHandler = nil
        try? output.close()
        try? input.close()
        // Partial files stay, so the next link resumes them from where this one stopped.
        downloads.values.forEach { $0.suspend() }
        downloads.removeAll()
        engine.ended(self)
        onClose()
    }
}

/// Reads one file out in chunks as the link has room. Lives on the session's write queue.
final class Upload {
    let hash: String
    let index: Int
    private let handle: FileHandle
    private var offset: Int64
    let total: Int64
    var sent: Int64 { offset }
    private var sentEmpty = false
    var done: Bool { offset >= total && (total > 0 || sentEmpty) }

    init?(hash: String, index: Int, path: String, offset: Int64) {
        guard let h = FileHandle(forReadingAtPath: path),
              let size = (try? FileManager.default.attributesOfItem(atPath: path)[.size] as? NSNumber)?.int64Value,
              offset >= 0, offset <= size else { return nil }
        try? h.seek(toOffset: UInt64(offset))
        self.hash = hash
        self.index = index
        self.handle = h
        self.offset = offset
        self.total = size
    }

    deinit { try? handle.close() }

    /// nil when the file shrank under us: the transfer is cancelled rather than sent short.
    func nextChunk() -> Frame? {
        let n = Int(min(Int64(Sync.chunkSize), total - offset))
        let bytes = n > 0 ? ((try? handle.read(upToCount: n)) ?? Data()) : Data()
        guard bytes.count == n else { return nil }
        let f = Frame.chunk(hash: hash, index: index, offset: offset, total: total, bytes: bytes)
        offset += Int64(n)
        if n == 0 { sentEmpty = true }
        return f
    }
}

/// An item's image or files arriving, one file after another, into a temp file and then their final place.
final class Download {
    enum Step { case more, nextFile, done, failed }
    let hash: String
    let kind: String
    let names: [String]
    let shas: [String]
    let sizes: [Int64]
    let from: String
    let apply: Bool
    var total: Int64 { sizes.reduce(0, +) }
    var doneBytes: Int64 { sizes.prefix(index).reduce(0, +) + received }
    private(set) var index = 0
    private(set) var received: Int64 = 0
    private(set) var paths: [String] = []
    private var handle: FileHandle?
    private var temp: URL { Paths.dir.appendingPathComponent("incoming/\(sha(hash))-\(index)") }

    init(hash: String, kind: String, names: [String], shas: [String], sizes: [Int64], from: String, apply: Bool) {
        self.hash = hash
        self.kind = kind
        self.names = names
        self.shas = shas
        self.sizes = sizes
        self.from = from
        self.apply = apply
        // A partial first file from a link that dropped: carry on from its end.
        if let size = (try? FileManager.default.attributesOfItem(atPath: temp.path)[.size] as? NSNumber)?.int64Value,
           size <= sizes.first ?? 0 {
            received = size
        }
    }

    func write(_ bytes: Data, total: Int64) -> Step {
        guard index < names.count, received + Int64(bytes.count) <= total else { return .failed }
        if handle == nil {
            try? FileManager.default.createDirectory(at: temp.deletingLastPathComponent(), withIntermediateDirectories: true)
            if !FileManager.default.fileExists(atPath: temp.path) {
                FileManager.default.createFile(atPath: temp.path, contents: nil, attributes: [.posixPermissions: 0o600])
            }
            handle = FileHandle(forWritingAtPath: temp.path)
            try? handle?.truncate(atOffset: UInt64(received))
        }
        guard let h = handle, (try? h.write(contentsOf: bytes)) != nil else { return .failed }
        received += Int64(bytes.count)
        guard received == total else { return .more }
        try? h.close()
        handle = nil
        guard let dest = place() else { return .failed }
        paths.append(dest)
        index += 1
        received = 0
        return index < names.count ? .nextFile : .done
    }

    /// Images go next to ClipBubble's own; files go to Downloads/ClipBubble/<device>/ under a safe, unique name.
    private func place() -> String? {
        if kind == "image" {
            let expected = shas[safe: index] ?? ""
            guard let data = FileManager.default.contents(atPath: temp.path), expected.isEmpty || sha(data) == expected else {
                try? FileManager.default.removeItem(at: temp)
                return nil
            }
            let dest = Paths.images.appendingPathComponent(sha(data) + ".png")
            try? FileManager.default.removeItem(at: dest)
            guard (try? FileManager.default.moveItem(at: temp, to: dest)) != nil else { return nil }
            return dest.path
        }
        let dir = receivedFolder(from)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let dest = uniqueURL(in: dir, name: safeFileName(names[index]))
        guard (try? FileManager.default.moveItem(at: temp, to: dest)) != nil else { return nil }
        return dest.path
    }

    func suspend() {
        try? handle?.close()
        handle = nil
    }

    func discard() {
        suspend()
        try? FileManager.default.removeItem(at: temp)
    }
}

/// A file moving between devices, for progress in the panel.
struct Transfer: Equatable {
    var done: Int64
    var total: Int64
    var peer: String
    var sending: Bool
    var fraction: Double { total > 0 ? min(1, Double(done) / Double(total)) : 0 }
}

/// Where files from another device land.
func receivedFolder(_ device: String) -> URL {
    let downloads = ProcessInfo.processInfo.environment["CLIPBUBBLE_DOWNLOADS"].map { URL(fileURLWithPath: $0) }
        ?? Platform.downloads
    return downloads.appendingPathComponent("ClipBubble").appendingPathComponent(safeFileName(device))
}

/// A name from another machine can't climb out of its folder or hide itself.
func safeFileName(_ raw: String) -> String {
    var s = raw.replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "\\", with: "_")
        .replacingOccurrences(of: ":", with: "_").filter { !$0.isNewline && $0 != "\0" }
    while s.hasPrefix(".") { s.removeFirst() }
    s = String(s.prefix(200)).trimmingCharacters(in: .whitespaces)
    return s.isEmpty ? "file" : s
}

func uniqueURL(in dir: URL, name: String) -> URL {
    var url = dir.appendingPathComponent(name)
    let base = (name as NSString).deletingPathExtension, ext = (name as NSString).pathExtension
    var n = 2
    while FileManager.default.fileExists(atPath: url.path) {
        url = dir.appendingPathComponent(ext.isEmpty ? "\(base) (\(n))" : "\(base) (\(n)).\(ext)")
        n += 1
    }
    return url
}

extension Array {
    subscript(safe i: Int) -> Element? { indices.contains(i) ? self[i] : nil }
}

// MARK: - Log

enum SyncLog {
    private static let queue = DispatchQueue(label: "clipbubble.synclog", qos: .background)
    static func write(_ line: String) {
        let stamp = ISO8601DateFormatter().string(from: Date())
        queue.async {
            let url = Paths.dir.appendingPathComponent("sync.log")
            if let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.intValue, size > 1_000_000 {
                try? FileManager.default.removeItem(at: url.appendingPathExtension("1"))
                try? FileManager.default.moveItem(at: url, to: url.appendingPathExtension("1"))
            }
            if !FileManager.default.fileExists(atPath: url.path) { FileManager.default.createFile(atPath: url.path, contents: nil) }
            guard let h = FileHandle(forWritingAtPath: url.path) else { return }
            h.seekToEndOfFile()
            h.write(Data("\(stamp) \(line)\n".utf8))
            try? h.close()
        }
    }
}
