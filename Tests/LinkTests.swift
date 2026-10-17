import AppKit

/// The other side of a link test: a second engine in its own process, with its own data, on stdin and stdout.
func runChild() -> Never {
    let env = ProcessInfo.processInfo.environment
    let db = DB()
    if env["CLIPBUBBLE_CHILD"] == "dump" {
        for c in db.clips(limit: 1000) {
            let exists = (c.file ?? c.paths.first).map { FileManager.default.fileExists(atPath: $0) } ?? false
            print("clip|\(c.hash)|\(c.origin)|\(c.remote)|\(exists)")
        }
        for s in db.stamps() where s.deleted { print("deleted|\(s.hash)") }
        exit(0)
    }
    let minuteAgo = Date().addingTimeInterval(-60)
    if let t = env["SEED_TEXT"] { db.insertIfMissing(kind: "text", text: t, source: "Terminal", hash: "t:" + sha(t), created: minuteAgo) }
    if let f = env["SEED_FILE"] { db.insertIfMissing(kind: "file", text: f, source: "Files", hash: "f:" + sha(f), created: minuteAgo) }
    let applied = Paths.dir.appendingPathComponent("applied.txt")
    SyncEngine.shared.apply = { c in
        let h = FileHandle(forWritingAtPath: applied.path) ?? { FileManager.default.createFile(atPath: applied.path, contents: nil); return FileHandle(forWritingAtPath: applied.path)! }()
        h.seekToEndOfFile()
        h.write(Data((c.hash + "\n").utf8))
        try? h.close()
    }
    SyncEngine.shared.serveStdio()
    dispatchMain()
}

private func waitFor(_ what: String, seconds: Double = 8, _ cond: () -> Bool) -> Bool {
    let end = Date().addingTimeInterval(seconds)
    while Date() < end {
        if cond() { return true }
        usleep(50_000)
    }
    print("timed out waiting for \(what)")
    return false
}

private func childEnv(_ home: URL, _ extra: [String: String]) -> [String: String] {
    var env = ProcessInfo.processInfo.environment
    env["CLIPBUBBLE_HOME"] = home.path
    env["CLIPBUBBLE_DOWNLOADS"] = home.appendingPathComponent("Downloads").path
    env["CLIPBUBBLE_DEVICE_NAME"] = "Peer"
    for (k, v) in extra { env[k] = v }
    return env
}

func linkTests() {
    check(safeFileName("../../.ssh/authorized_keys") == "_.._.ssh_authorized_keys", "names can't climb out of their folder")
    check(safeFileName("...hidden") == "hidden", "names can't hide")
    check(safeFileName("") == "file", "empty names get a name")

    let peerHome = testHome.appendingPathComponent("peer")
    try? FileManager.default.createDirectory(at: peerHome, withIntermediateDirectories: true)
    let engine = SyncEngine.shared
    let lock = NSLock()
    var applied: [String] = []
    engine.apply = { c in lock.lock(); applied.append(c.hash); lock.unlock() }

    let db = DB()
    db.upsert(kind: "text", text: "mac-old", file: nil, source: "Notes", hash: "t:" + sha("mac-old"))
    let big = testHome.appendingPathComponent("big file.bin")
    let bigData = Data((0..<600_000).map { UInt8(truncatingIfNeeded: $0 &* 31) })
    try? bigData.write(to: big)

    let p = Process()
    p.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    p.environment = childEnv(peerHome, ["CLIPBUBBLE_CHILD": "serve", "SEED_TEXT": "peer-old", "SEED_FILE": big.path])
    let toChild = Pipe(), fromChild = Pipe()
    p.standardInput = toChild
    p.standardOutput = fromChild
    try? p.run()
    engine.queue.sync { engine.begin(Session(input: fromChild.fileHandleForReading, output: toChild.fileHandleForWriting, engine: engine)) }

    // Reconcile: each side gets the other's history, and the file under 25 MB comes over on its own.
    let peerText = "t:" + sha("peer-old"), fileHash = "f:" + sha(big.path)
    check(waitFor("peer's text") { db.clip(hash: peerText) != nil }, "peer's history arrives")
    check(db.clip(hash: peerText).map { !$0.origin.isEmpty } ?? false, "it's marked as from the peer")
    check(db.deviceNames().values.contains("Peer"), "the peer's name is saved")
    check(waitFor("peer's file") { db.clip(hash: fileHash)?.remote == false }, "a 600 KB file arrives in chunks")
    let landed = db.clip(hash: fileHash)?.paths.first
    check(landed.flatMap { FileManager.default.contents(atPath: $0) } == bigData, "the file arrives intact")
    check(landed?.hasSuffix("Downloads/ClipBubble/Peer/big file.bin") ?? false, "it lands in Downloads/ClipBubble/<device>")
    check(applied.isEmpty, "history older than 10 s doesn't touch the clipboard")

    // Live: a new copy here goes on the peer's clipboard, and isn't echoed back.
    var changes: [String] = []
    let hook = DB.changed
    DB.changed = { h in lock.lock(); changes += h; lock.unlock(); hook?(h) }
    let live = "live copy \(Date().timeIntervalSince1970)", liveHash = "t:" + sha(live)
    db.upsert(kind: "text", text: live, file: nil, source: "Safari", hash: liveHash)
    let appliedFile = peerHome.appendingPathComponent("applied.txt")
    check(waitFor("peer applies") { (try? String(contentsOf: appliedFile, encoding: .utf8))?.contains(liveHash) ?? false },
          "a new copy lands on the peer's clipboard")
    usleep(1_500_000)
    lock.lock()
    check(changes.filter { $0 == liveHash }.count == 1, "no echo: the copy changed this history once")
    lock.unlock()

    // An image moves as bytes and is checked against its hash.
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 8, pixelsHigh: 8, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.setColor(NSColor(deviceRed: 1, green: 0.2, blue: 0.5, alpha: 1), atX: 3, y: 3)
    let png = rep.representation(using: .png, properties: [:])!
    let imgPath = Paths.images.appendingPathComponent(sha(png) + ".png")
    try? png.write(to: imgPath)
    db.upsert(kind: "image", text: "", file: imgPath.path, source: "Preview", hash: "i:" + sha(png))
    check(waitFor("peer applies image") { (try? String(contentsOf: appliedFile, encoding: .utf8))?.contains("i:" + sha(png)) ?? false },
          "a new image lands on the peer's clipboard once its bytes arrive")

    // A delete here removes it there.
    if let c = db.clip(hash: liveHash) { db.delete(id: c.id) }
    usleep(500_000)
    try? toChild.fileHandleForWriting.close()
    p.waitUntilExit()

    let dump = Process()
    dump.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    dump.environment = childEnv(peerHome, ["CLIPBUBBLE_CHILD": "dump"])
    let out = Pipe()
    dump.standardOutput = out
    try? dump.run()
    let lines = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).split(separator: "\n").map(String.init)
    dump.waitUntilExit()
    check(lines.contains { $0.hasPrefix("clip|t:\(sha("mac-old"))|") && !$0.hasSuffix("||false|false") }, "the peer got this Mac's history")
    check(lines.contains("deleted|\(liveHash)") && !lines.contains { $0.hasPrefix("clip|\(liveHash)|") }, "the delete reached the peer")
    check(lines.contains { $0.hasPrefix("clip|i:\(sha(png))|") && $0.hasSuffix("|false|true") }, "the image is stored on the peer")
    DB.changed = hook
}
