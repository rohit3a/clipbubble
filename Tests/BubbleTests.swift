import AppKit

/// Two displays past the right edge of every real one: the windows are real, and a run never shows on screen.
private let stage: [Display] = {
    let x = (NSScreen.screens.map(\.frame.maxX).max() ?? 0) + 4000
    return (0..<2).map { i in
        let frame = NSRect(x: x + CGFloat(i) * 1500, y: 0, width: 1400, height: 900)
        return Display(id: 100 + i, frame: frame, visible: frame.insetBy(dx: 0, dy: 30))
    }
}()
private var strayed = false

private func pump(_ seconds: TimeInterval) {
    RunLoop.main.run(until: Date().addingTimeInterval(seconds))
    let real = NSScreen.screens.map(\.frame)
    if NSApp.isActive || NSApp.keyWindow != nil || NSApp.windows.contains(where: { w in w.isVisible && real.contains { $0.intersects(w.frame) } }) {
        strayed = true
    }
}

private func until(_ seconds: Double = 8, _ cond: () -> Bool) -> Bool {
    let end = Date().addingTimeInterval(seconds)
    while Date() < end {
        if cond() { return true }
        pump(0.02)
    }
    return cond()
}

/// The app with real windows and a real Capture, on displays and a clipboard of their own: these tests never touch the real ones.
private final class Rig {
    let d = AppDelegate()
    let board = NSPasteboard(name: .init("clipbubble-tests-" + UUID().uuidString))
    let bubble: NSWindow, panel: NSWindow
    let view: BubbleView
    let windows: [NSWindow]
    var changes = 0
    private var capture: Capture?

    init() {
        let before = Set(NSApp.windows.map(ObjectIdentifier.init))
        d.displays = { stage }
        d.setUpWindows(keyable: false)
        windows = NSApp.windows.filter { !before.contains(ObjectIdentifier($0)) }
        bubble = windows.first { $0.contentView is BubbleView }!
        panel = windows.first { $0.frame.width == 420 }!
        view = bubble.contentView as! BubbleView
        d.putOnClipboard = { [board] c in
            board.clearContents()
            switch c.kind {
            case "text": board.setString(c.text, forType: .string)
            case "image": board.setData(FileManager.default.contents(atPath: c.file ?? ""), forType: .png)
            default: board.writeObjects(c.paths.map { URL(fileURLWithPath: $0) as NSURL })
            }
            return board.changeCount
        }
        capture = Capture(board: board) { [weak self] copied in
            DispatchQueue.main.async { self?.changes += 1; self?.d.clipboardChanged(copied: copied) }
        }
    }

    var model: Model { d.model }
    var db: DB { d.model.db }

    func put(_ text: String, marked type: String? = nil) {
        board.clearContents()
        board.setString(text, forType: .string)
        if let type { board.setString("", forType: .init(type)) }
    }

    /// Puts text on the clipboard and waits until capture has dealt with it.
    func copy(_ text: String) {
        let before = changes
        put(text)
        if !until(8, { changes > before }) { print("capture never saw \(text)") }
    }

    func row(_ text: String) -> Clip { db.clip(hash: "t:" + sha(text))! }
    var history: [String] { db.clips(limit: 5).map(\.text) }
    /// Long enough for capture to have noticed a change it's going to act on.
    func settle() { pump(1.6) }
    func close() { capture = nil; board.releaseGlobally(); windows.forEach { $0.orderOut(nil) } }
}

func bubbleTests() {
    _ = NSApplication.shared
    NSApp.setActivationPolicy(.accessory)
    let r = Rig()
    defer { r.close() }
    pickTests(r)
    check(!strayed, "no window reached a real display, and the app never took focus")
}

private func pickTests(_ r: Rig) {
    r.db.deleteAll()
    var told: [String] = []
    let hook = DB.changed
    DB.changed = { told += $0; hook?($0) }
    defer { DB.changed = hook }

    // Picking the previous item right after a copy keeps the copy.
    r.copy("older")
    r.copy("newer")
    r.model.unseen = 0
    told = []
    r.d.pick(r.row("older"))
    r.settle()
    check(r.history == ["older", "newer"], "picking the previous item keeps the newest copy, got \(r.history)")
    check(r.db.stamp(hash: "t:" + sha("newer"))?.deleted == false, "and no delete goes out to linked devices")
    check(told == ["t:" + sha("older")], "the pick reaches linked devices, once, got \(told.count)")
    check(r.model.unseen == 0 && !r.model.pulse, "a pick isn't a new copy")

    // The copy it was hiding can be copied again.
    r.copy("newer")
    check(r.history == ["newer", "older"], "re-copying the item after a pick counts, got \(r.history)")

    // A stored image survives too.
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 4, pixelsHigh: 4, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    let png = rep.representation(using: .png, properties: [:])!
    r.copy("before the image")
    let before = r.changes
    r.board.clearContents()
    r.board.setData(png, forType: .png)
    check(until { r.changes > before }, "the image is captured")
    let stored = r.db.clip(hash: "i:" + sha(png))?.file ?? ""
    r.d.pick(r.row("before the image"))
    r.settle()
    check(FileManager.default.fileExists(atPath: stored), "picking the previous item keeps a stored image")
    check(r.db.clip(hash: "i:" + sha(png)) != nil, "and its row")

    // Paste-through tools are still undone, without looking like a copy.
    r.copy("kept")
    r.copy("tool text")
    r.model.unseen = 0
    r.copy("kept")
    check(r.db.clip(hash: "t:" + sha("tool text")) == nil, "a paste-through is still rolled back")
    check(r.model.unseen == 0, "and undoing it doesn't touch the badge")
    r.d.pick(r.row("older"))
    r.settle()
    r.copy("tool text 2")
    r.copy("older")
    check(r.db.clip(hash: "t:" + sha("tool text 2")) == nil, "and it's rolled back after a pick too")

    // A pick moves the item up and leaves it as it was.
    r.db.upsert(kind: "text", text: "from the phone", file: nil, source: "iPhone", hash: "t:" + sha("from the phone"))
    r.copy("on top")
    r.d.pick(r.row("from the phone"))
    r.settle()
    check(r.db.clips(limit: 1).first?.source == "iPhone", "a pick keeps the item's source")
    check(r.db.clips(source: "iPhone").count == 1, "so it stays in the iPhone filter")

    // A file from another device: the copy has a path here, and a different hash.
    let file = testHome.appendingPathComponent("received.txt")
    FileManager.default.createFile(atPath: file.path, contents: Data("hi".utf8))
    let sent = Entry(hash: "f:from-other-mac", time: Date().timeIntervalSince1970 - 60, kind: "file", text: "received.txt", source: "Finder",
                     origin: "other-mac", blobs: [Blob(name: "received.txt", size: 2, sha: "")])
    r.db.insert(sent, origin: "other-mac")
    r.db.received(hash: sent.hash, file: nil, text: file.path)
    r.d.pick(r.db.clip(hash: sent.hash)!)
    r.settle()
    check(r.db.clips(kind: "file").count == 1, "picking a received file doesn't make a second row")
    check(r.db.clips(limit: 1).first?.hash == sent.hash, "and moves it up")

    // Still on the other device: start getting it, and stay put.
    var wrote = false
    let write = r.d.putOnClipboard
    r.d.putOnClipboard = { wrote = true; return write($0) }
    r.db.insert(Entry(hash: "f:still-there", time: Date().timeIntervalSince1970, kind: "file", text: "far.bin", source: "Finder", origin: "other-mac",
                      blobs: [Blob(name: "far.bin", size: 2, sha: "")]), origin: "other-mac")
    let far = r.db.clip(hash: "f:still-there")!
    r.d.toggle()
    r.d.pick(far)
    check(!wrote && r.panel.isVisible, "a row that isn't here yet stays in the panel and leaves the clipboard alone")
    r.d.toggle()
    r.d.putOnClipboard = write
}

private extension AppDelegate {
    func pick(_ c: Clip) { model.pick(c, false) }
}
