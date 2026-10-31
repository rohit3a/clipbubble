import Foundation

/// Plays a linked device by hand, so a test can send what no honest engine would.
private final class RawPeer {
    private let toEngine = Pipe(), fromEngine = Pipe()
    private let lock = NSLock()
    private var reader = FrameReader()
    private var frames: [Frame] = []
    let session: Session

    init(device: String = "raw-peer", summary: [Stamp] = []) {
        session = Session(input: toEngine.fileHandleForReading, output: fromEngine.fileHandleForWriting, engine: .shared)
        fromEngine.fileHandleForReading.readabilityHandler = { [self] h in
            let d = h.availableData
            lock.lock()
            frames += (try? reader.feed(d)) ?? []
            lock.unlock()
        }
        SyncEngine.shared.queue.sync { SyncEngine.shared.begin(session) }
        send(.hello(Hello(proto: Sync.proto, device: device, name: "Lost iPhone", os: "ios", version: "1.6.2", clock: Date().timeIntervalSince1970)))
        send(.summary(summary))
        _ = first { if case .summary = $0 { true } else { false } }
    }

    func send(_ f: Frame) { try? toEngine.fileHandleForWriting.write(contentsOf: f.encoded()) }

    func first(_ match: (Frame) -> Bool) -> Frame? {
        let end = Date().addingTimeInterval(8)
        while Date() < end {
            lock.lock()
            let hit = frames.first(where: match)
            lock.unlock()
            if let hit { return hit }
            usleep(10_000)
        }
        return nil
    }

    func has(_ hash: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return frames.contains { if case .put(let e) = $0 { e.hash == hash } else { false } }
    }

    func put(_ hash: String) -> Entry? {
        if case .put(let e)? = first({ if case .put(let e) = $0 { e.hash == hash } else { false } }) { return e }
        return nil
    }

    /// Fetches and says what came back: the bytes of a chunk, or nil for a cancel.
    func fetch(_ hash: String, index: Int = 0, offset: Int64 = 0) -> Data? {
        send(.fetch(hash: hash, index: index, offset: offset))
        let reply = first {
            switch $0 {
            case .chunk(let h, _, _, _, _), .cancel(let h): h == hash
            default: false
            }
        }
        lock.lock()
        frames.removeAll { $0 == reply }
        lock.unlock()
        if case .chunk(_, _, _, _, let bytes)? = reply { return bytes }
        check(reply != nil, "the engine answers a fetch of \(hash)")
        return nil
    }

    func close() {
        fromEngine.fileHandleForReading.readabilityHandler = nil
        SyncEngine.shared.queue.sync { session.close() }
    }
}

/// docs/sync-protocol.md: a device reaches only files ClipBubble offered it from history.
func boundaryTests() {
    let db = DB()
    let secret = testHome.appendingPathComponent("secret.txt")
    let secretData = Data("not for peers".utf8)
    try? secretData.write(to: secret)
    let link = testHome.appendingPathComponent("innocent.txt")
    try? FileManager.default.createSymbolicLink(at: link, withDestinationURL: secret)
    let cwd = FileManager.default.currentDirectoryPath   // later suites relaunch this binary by its relative path
    FileManager.default.changeCurrentDirectoryPath(testHome.path)
    defer { FileManager.default.changeCurrentDirectoryPath(cwd) }

    let peer = RawPeer()
    let engine = SyncEngine.shared
    var applied: [String] = []
    let lock = NSLock()
    engine.apply = { c in lock.lock(); applied.append(c.hash); lock.unlock() }

    let names = [secret.path, "../" + testHome.lastPathComponent + "/secret.txt", "./secret.txt", "secret.txt", "innocent.txt", link.path,
                 "~/secret.txt", "file://" + secret.path, "//" + secret.path, "/" + secret.path, " " + secret.path, "safe.txt\n" + secret.path,
                 "\\" + secret.path, secret.path + "\r"]
    for (i, text) in names.enumerated() {
        let hash = "f:probe\(i)"
        peer.send(.put(Entry(hash: hash, time: Date().timeIntervalSince1970, kind: "file", text: text, source: "Finder", origin: engine.device, blobs: [])))
        check(peer.fetch(hash) == nil, "a peer's text is never a path: \(text)")
        check(peer.fetch(hash, index: 1) == nil, "nor is a later index: \(text)")
        let row = db.clip(hash: hash)
        check(row?.paths.allSatisfy { !$0.contains("/") } ?? false, "the row keeps bare names: \(row?.text ?? "no row")")
    }
    check(applied.isEmpty, "a file item with no bytes never lands on the clipboard")

    // The row is waiting for bytes, but the names in it are still the peer's.
    let waiting = "f:waiting"
    peer.send(.put(Entry(hash: waiting, time: Date().timeIntervalSince1970, kind: "file", text: secret.path, source: "Finder", origin: "elsewhere",
                         blobs: [Blob(name: "../../secret.txt", size: 5, sha: "")])))
    check(peer.fetch(waiting) == nil, "a row still waiting for its bytes can't be fetched")
    check(db.clip(hash: waiting)?.text == safeFileName(secret.path), "and holds a bare name")
    peer.send(.cancel(hash: waiting))
    peer.close()

    // Real items in this history, this time.
    let mine = testHome.appendingPathComponent("mine.txt")
    let mineData = Data("offered".utf8)
    try? mineData.write(to: mine)
    db.upsert(kind: "file", text: mine.path, file: nil, source: "Finder", hash: "f:" + sha(mine.path))
    let mineHash = "f:" + sha(mine.path)

    let liar = RawPeer(device: "liar", summary: [db.stamp(hash: mineHash)!])
    check(liar.fetch(mineHash) == nil, "claiming to know an item in a summary doesn't offer it")
    check(liar.fetch("f:" + sha(secret.path)) == nil, "the hash of something never in history gets nothing")
    liar.close()

    let honest = RawPeer(device: "honest")
    check(honest.put(mineHash)?.blobs.count == 1, "an item in history is offered")
    check(honest.put(mineHash)?.text == "mine.txt", "by name, not by path")
    check(honest.fetch(mineHash) == mineData, "and an offered item comes over")
    check(honest.fetch(mineHash, offset: -1) == nil, "a negative offset gets nothing")
    check(honest.fetch(mineHash, offset: Int64(mineData.count) + 1) == nil, "nor does one past the end")
    check(honest.fetch(mineHash, index: 1 << 31) == nil, "nor does an index past the end")
    db.delete(hash: mineHash)
    check(honest.fetch(mineHash) == nil, "a delete takes it back at once")
    honest.close()

    let shot = testHome.appendingPathComponent("shot.txt")
    try? Data("hidden".utf8).write(to: shot)
    let shotHash = "f:" + sha(shot.path)
    db.upsert(kind: "file", text: shot.path, file: nil, source: "Finder", hash: shotHash)
    let early = RawPeer(device: "early")
    check(early.put(shotHash) != nil, "offered while its switch is on")
    Prefs.set("files", false)
    check(early.fetch(shotHash) == nil, "switching a category off takes back what was offered")
    let hidden = RawPeer(device: "hidden")
    check(hidden.fetch(shotHash) == nil, "and it isn't offered to the next device")
    Prefs.set("files", true)
    hidden.close()
    early.close()
    db.delete(hash: shotHash)

    // Three devices: this one holds an item from another that its bytes haven't reached yet.
    let pic = testHome.appendingPathComponent("relayed.png")
    let picData = Data("relayed picture".utf8)
    try? picData.write(to: pic)
    let picHash = "i:" + sha(picData)
    let now = Date().timeIntervalSince1970
    db.insert(Entry(hash: picHash, time: now - 60, kind: "image", text: "", source: "Preview", origin: "elsewhere",
                    blobs: [Blob(name: "relayed.png", size: Int64(picData.count), sha: String(picHash.dropFirst(2)))]), origin: "elsewhere")
    let marker = "after the waiting row \(now)"
    db.upsert(kind: "text", text: marker, file: nil, source: "Notes", hash: "t:" + sha(marker))
    let third = RawPeer(device: "third")
    check(third.put("t:" + sha(marker)) != nil, "the third device gets what this one holds")
    check(!third.has(picHash), "but no item with its bytes still on the way")
    db.received(hash: picHash, file: pic.path, text: nil)
    check(third.put(picHash)?.blobs.count == 1, "it's offered as soon as they arrive")
    check(third.fetch(picHash) == picData, "and comes over")
    third.close()
    db.delete(hash: picHash)
    engine.apply = { _ in }
}
