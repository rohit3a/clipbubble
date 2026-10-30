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
        for d in db.devices() { print("device|\(d.id)|\(d.name)") }
        exit(0)
    }
    // A device this side knows only by name, as a Mac knows a Linux box the iPhone has never linked with.
    if let d = env["SEED_DEVICE"]?.split(separator: "|").map(String.init), d.count == 3 { db.saveDevice(id: d[0], name: d[1], os: d[2]) }
    let minuteAgo = Date().addingTimeInterval(-60)
    if let t = env["SEED_TEXT"] { db.insertIfMissing(kind: "text", text: t, source: "Terminal", hash: "t:" + sha(t), created: minuteAgo) }
    for key in ["SEED_FILE", "SEED_BIG"] {
        if let f = env[key] { db.insertIfMissing(kind: "file", text: f, source: "Files", hash: "f:" + sha(f), created: minuteAgo) }
    }
    let applied = Paths.dir.appendingPathComponent("applied.txt")
    SyncEngine.shared.apply = { c in
        let h = FileHandle(forWritingAtPath: applied.path) ?? { FileManager.default.createFile(atPath: applied.path, contents: nil); return FileHandle(forWritingAtPath: applied.path)! }()
        h.seekToEndOfFile()
        h.write(Data((c.hash + "\n").utf8))
        try? h.close()
    }
    SyncEngine.shared.serveStdio()
    // A copy made after linking, for peers in other languages to receive live.
    if let t = env["LIVE_TEXT"] {
        DispatchQueue.global().asyncAfter(deadline: .now() + 1.5) {
            DB().upsert(kind: "text", text: t, file: nil, source: "Notes", hash: "t:" + sha(t))
        }
    }
    if let f = env["LIVE_FILE"] {
        DispatchQueue.global().asyncAfter(deadline: .now() + 1.5) {
            DB().upsert(kind: "file", text: f, file: nil, source: "Finder", hash: "f:" + sha(f))
        }
    }
    if let t = env["DELETE_TEXT"] {
        DispatchQueue.global().asyncAfter(deadline: .now() + 1.5) {
            let d = DB()
            for c in d.clips(limit: 500) where c.text.hasPrefix(t) { d.delete(id: c.id) }
        }
    }
    dispatchMain()
}

private func waitFor(_ what: String, seconds: Double = 8, _ cond: () -> Bool) -> Bool {
    let end = Date().addingTimeInterval(seconds)
    while Date() < end {
        if cond() { return true }
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))   // lets main-thread callbacks like onTransfers run
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
    // Over 25 MB: these wait for Get on this side and Send to from this side.
    let huge = testHome.appendingPathComponent("huge.bin"), hugeHere = testHome.appendingPathComponent("huge here.bin")
    let hugeData = Data((0..<(26 << 20)).map { UInt8(truncatingIfNeeded: $0 &* 7 &+ $0 >> 13) })
    try? hugeData.write(to: huge)
    try? Data(hugeData.reversed()).write(to: hugeHere)
    db.upsert(kind: "file", text: hugeHere.path, file: nil, source: "Finder", hash: "f:" + sha(hugeHere.path))

    let p = Process()
    p.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    p.environment = childEnv(peerHome, ["CLIPBUBBLE_CHILD": "serve", "SEED_TEXT": "peer-old", "SEED_FILE": big.path, "SEED_BIG": huge.path,
                                         "SEED_DEVICE": "rog-relayed|ROG|linux"])
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
    check(waitFor("relayed name") { db.deviceNames()["rog-relayed"] == "ROG" }, "a device the peer knows arrives with its name")
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

    // Over 25 MB waits for Get, and picks up a partial file a dropped link left behind.
    let hugeHash = "f:" + sha(huge.path)
    check(waitFor("huge offered") { db.clip(hash: hugeHash) != nil }, "a file over 25 MB is listed")
    usleep(500_000)
    check(db.clip(hash: hugeHash)?.remote == true, "but doesn't download on its own")
    let partial = Paths.dir.appendingPathComponent("incoming/\(sha(hugeHash))-0")
    try? FileManager.default.createDirectory(at: partial.deletingLastPathComponent(), withIntermediateDirectories: true)
    var head = hugeData.prefix(5 << 20)
    head[head.startIndex] ^= 0xFF   // marks the partial file, so the result shows it was kept
    try? head.write(to: partial)
    var seen: [Transfer] = []
    engine.onTransfers = { t in if let x = t[hugeHash] { seen.append(x) } }
    engine.get(hugeHash)
    check(waitFor("huge arrives", seconds: 30) { db.clip(hash: hugeHash)?.remote == false }, "Get downloads it")
    let got = db.clip(hash: hugeHash)?.paths.first.flatMap { FileManager.default.contents(atPath: $0) }
    check(got?.count == hugeData.count && got?.first == head.first && got?.dropFirst() == hugeData.dropFirst(), "it resumed from the partial file")
    check(seen.contains { !$0.sending && $0.total == Int64(26 << 20) && $0.done >= Int64(5 << 20) }, "progress counts from the resumed offset")

    // Send to: this side asks the peer to take a big file now.
    let peerDevice = (try? String(contentsOf: peerHome.appendingPathComponent("device-id"), encoding: .utf8)) ?? ""
    seen = []
    engine.onTransfers = { t in if let x = t["f:" + sha(hugeHere.path)] { seen.append(x) } }
    engine.send("f:" + sha(hugeHere.path), toDevice: peerDevice)
    check(waitFor("upload progress", seconds: 30) { seen.contains { $0.sending && $0.total == Int64(26 << 20) } }, "sending shows progress")
    let hugeHereFile = peerHome.appendingPathComponent("Downloads/ClipBubble/\(engine.name)/huge here.bin")
    check(waitFor("peer gets huge", seconds: 30) { (try? FileManager.default.attributesOfItem(atPath: hugeHereFile.path)[.size] as? Int) == hugeData.count },
          "Send to puts it on the peer")

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

    // The same device dialling in again replaces its old link instead of being turned away.
    func dialIn() -> (Process, Pipe) {
        let p = Process(), toChild = Pipe(), fromChild = Pipe()
        p.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
        p.environment = childEnv(peerHome, ["CLIPBUBBLE_CHILD": "serve"])
        p.standardInput = toChild
        p.standardOutput = fromChild
        try? p.run()
        engine.queue.sync { engine.begin(Session(input: fromChild.fileHandleForReading, output: toChild.fileHandleForWriting, engine: engine)) }
        return (p, toChild)
    }
    let (first, _) = dialIn()
    check(waitFor("first link") { engine.linked.count == 1 }, "the device links")
    let (second, secondIn) = dialIn()
    check(waitFor("old link closes") { !first.isRunning }, "dialling in again closes its old link")
    let again = "after redial \(Date().timeIntervalSince1970)"
    db.upsert(kind: "text", text: again, file: nil, source: "Notes", hash: "t:" + sha(again))
    check(waitFor("new link applies") { (try? String(contentsOf: appliedFile, encoding: .utf8))?.contains("t:" + sha(again)) ?? false },
          "and the new link carries copies")

    // A row too big for a frame stays here. Sent, it would make the far end drop the link on every reconnect.
    let tooBig = String(repeating: "x", count: Sync.maxFrame + 1), after = "after too big \(Date().timeIntervalSince1970)"
    db.upsert(kind: "text", text: tooBig, file: nil, source: "Notes", hash: "t:" + sha(tooBig))
    db.upsert(kind: "text", text: after, file: nil, source: "Notes", hash: "t:" + sha(after))
    let appliedNow = { (try? String(contentsOf: appliedFile, encoding: .utf8)) ?? "" }
    check(waitFor("copy after a big row") { appliedNow().contains("t:" + sha(after)) }, "a row too big to sync doesn't stop the next copy")
    check(!appliedNow().contains("t:" + sha(tooBig)) && second.isRunning, "and the link holds")

    // Two names for one computer: it keeps the newest link from this device, so this end must too, or they knock each other off.
    var pipes: [Pipe] = []
    func named(_ alias: String) -> (HostLink, FileHandle) {
        let incoming = Pipe(), outgoing = Pipe(), link = HostLink(alias: alias, engine: engine)
        pipes += [incoming, outgoing]
        let s = Session(input: incoming.fileHandleForReading, output: outgoing.fileHandleForWriting, engine: engine)
        s.link = link
        engine.queue.sync { engine.begin(s) }
        return (link, incoming.fileHandleForWriting)
    }
    let computer = Hello(proto: Sync.proto, device: "two-names", name: "Two", os: "macos", version: "9", clock: Date().timeIntervalSince1970)
    let links = { engine.linked.filter { $0.device == computer.device }.count }
    let (older, olderIn) = named("-older"), (newer, newerIn) = named("-newer")
    olderIn.write(Frame.hello(computer).encoded())
    check(waitFor("first name") { links() == 1 && engine.queue.sync { older.state.ok } }, "one name links")
    newerIn.write(Frame.hello(computer).encoded())
    check(waitFor("second name") { engine.queue.sync { newer.state.ok } }, "the second name takes over")
    check(waitFor("one link") { links() == 1 } && engine.queue.sync { older.state.ok }, "one link stays, and the first name reads as linked")
    try? olderIn.close()
    try? newerIn.close()

    try? secondIn.fileHandleForWriting.close()
    second.waitUntilExit()
    DB.changed = hook
}

/// Links the shared engine with a second engine in its own process.
func linkPeer(home: URL, _ extra: [String: String]) -> (Process, Pipe) {
    let engine = SyncEngine.shared
    let p = Process(), toChild = Pipe(), fromChild = Pipe()
    p.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    p.environment = childEnv(home, extra.merging(["CLIPBUBBLE_CHILD": "serve"]) { a, _ in a })
    p.standardInput = toChild
    p.standardOutput = fromChild
    try? p.run()
    engine.queue.sync { engine.begin(Session(input: fromChild.fileHandleForReading, output: toChild.fileHandleForWriting, engine: engine)) }
    return (p, toChild)
}

func unlinkPeer(_ l: (Process, Pipe)) {
    try? l.1.fileHandleForWriting.close()
    l.0.waitUntilExit()
    _ = waitFor("link gone") { SyncEngine.shared.linked.isEmpty }
}

/// What the bubble hears from sync: only an item arriving or moving up is news.
func syncBadgeTests() {
    let engine = SyncEngine.shared
    let db = DB()
    var news = 0, refreshes = 0
    engine.apply = { _ in }
    engine.onNew = { news += 1 }
    engine.onReceived = { refreshes += 1 }
    defer { engine.onNew = {}; engine.onReceived = {} }

    let peerHome = testHome.appendingPathComponent("badge-peer")
    try? FileManager.default.createDirectory(at: peerHome, withIntermediateDirectories: true)
    func link(_ extra: [String: String]) -> (Process, Pipe) { linkPeer(home: peerHome, extra) }
    func unlink(_ l: (Process, Pipe)) { unlinkPeer(l) }

    // A delete on the other device changes the list and nothing else.
    for i in 0..<5 { db.upsert(kind: "text", text: "doomed \(i)", file: nil, source: "Notes", hash: "t:" + sha("doomed \(i)")) }
    let doomed = link(["DELETE_TEXT": "doomed"])
    check(waitFor("deletes arrive") { !db.clips(limit: 500).contains { $0.text.hasPrefix("doomed") } }, "the peer's deletes arrive")
    check(waitFor("lists refresh") { refreshes > 0 }, "they refresh the lists")
    check(news == 0, "but aren't news, got \(news)")
    unlink(doomed)

    // One file from the other device is one item, however its bytes arrive.
    let small = testHome.appendingPathComponent("one.bin")
    try? Data(count: 200_000).write(to: small)
    news = 0
    let live = link(["LIVE_FILE": small.path])
    check(waitFor("file arrives") { db.clip(hash: "f:" + sha(small.path))?.remote == false }, "the peer's file arrives")
    RunLoop.main.run(until: Date().addingTimeInterval(0.5))
    check(news == 1, "a synced file is news once, got \(news)")
    unlink(live)

    // An item still waiting for its bytes is offered again on every link, and that isn't news.
    let huge = testHome.appendingPathComponent("waiting.bin")
    try? Data(count: 26 << 20).write(to: huge)
    news = 0
    let first = link(["SEED_BIG": huge.path])
    check(waitFor("waiting row") { db.clip(hash: "f:" + sha(huge.path))?.remote == true }, "the big file is listed")
    RunLoop.main.run(until: Date().addingTimeInterval(0.5))
    check(news == 1, "the first offer is news, got \(news)")
    unlink(first)
    news = 0
    let second = link([:])
    check(waitFor("relinked") { engine.linked.count == 1 }, "the peer links again")
    RunLoop.main.run(until: Date().addingTimeInterval(1))
    check(news == 0, "the same offer after a reconnect isn't, got \(news)")
    unlink(second)
}
