import AppKit
import Carbon.HIToolbox

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

private func key(_ code: Int, _ chars: String, _ mods: NSEvent.ModifierFlags) -> NSEvent {
    NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: mods, timestamp: 0, windowNumber: 0, context: nil,
                     characters: chars, charactersIgnoringModifiers: chars, isARepeat: false, keyCode: UInt16(code))!
}

/// The app with real windows and a real Capture, on displays and a clipboard of their own: these tests never touch the real ones.
private final class Rig {
    let d = AppDelegate()
    let board = NSPasteboard(name: .init("clipbubble-tests-" + UUID().uuidString))
    let bubble: NSWindow, panel: NSWindow
    let view: BubbleView
    let windows: [NSWindow]
    var pointer = NSPoint(x: stage[0].frame.midX, y: stage[0].frame.midY)
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
        d.pointer = { [unowned self] in pointer }
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
    var opaque: Bool { bubble.isVisible && bubble.alphaValue > 0.9 }

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

func shortcutTests() {
    let bubble = Shortcut(key(kVK_ANSI_B, "b", [.control, .option]))
    check(bubble == .bubble && bubble?.label == "⌃⌥B", "control-option-B records as the bubble shortcut")
    check(Shortcut(key(kVK_ANSI_K, "k", [.command, .shift]))?.label == "⇧⌘K", "command-shift-K records with its label")
    check(Shortcut(key(kVK_ANSI_C, "c", [.command])) == nil, "command-C alone would break copying")
    check(Shortcut(key(kVK_ANSI_B, "b", [.option, .shift])) == nil, "option-shift-B types a character")
    check(Shortcut(key(kVK_Tab, "\t", [.control, .option])) == nil, "a key with no label can't be a shortcut")

    UserDefaults.standard.removeObject(forKey: "bubbleHotkey")
    check(Shortcut.saved() == .bubble, "control-option-B is the default")
    Shortcut(key(kVK_ANSI_K, "k", [.command, .shift]))?.save()
    check(Shortcut.saved().label == "⇧⌘K", "a recorded shortcut survives a restart")
    UserDefaults.standard.set(Data("junk".utf8), forKey: "bubbleHotkey")
    check(Shortcut.saved() == .bubble, "an unreadable one falls back to the default")
    UserDefaults.standard.removeObject(forKey: "bubbleHotkey")
}

func bubbleTests() {
    _ = NSApplication.shared
    NSApp.setActivationPolicy(.accessory)
    let keys = ["hideBubble", "bubbleHotkey", "corner", "cornerDisplay"]
    keys.forEach(UserDefaults.standard.removeObject)
    defer { keys.forEach(UserDefaults.standard.removeObject) }
    AppDelegate.hideAfter = 0.3

    let r = Rig()
    defer { r.close() }
    let bubble = r.bubble
    r.db.deleteAll()

    // Off for existing users: nothing about the bubble changes.
    check(!r.model.hideBubble, "hide is off by default")
    check(bubble.isVisible, "the bubble shows at launch")
    r.copy("first")
    r.settle()
    check(bubble.isVisible, "and stays after a copy")

    // On: it goes, and that survives a restart.
    r.model.hideBubble = true
    check(until { !bubble.isVisible }, "switching it on hides the bubble once it's idle")
    let restarted = Rig()
    check(restarted.model.hideBubble, "the setting survives a restart")
    check(!restarted.bubble.isVisible, "and the bubble starts hidden")
    restarted.close()

    // A new copy brings it back, and it goes again.
    r.model.unseen = 0
    r.copy("second")
    check(until { r.opaque }, "a new copy shows the bubble")
    check(r.model.unseen == 1, "and counts on the badge")
    check(until { !bubble.isVisible }, "it hides again after the idle time")

    // These aren't something new.
    r.put("second")
    r.settle()
    check(!bubble.isVisible, "the same copy again doesn't show it")
    r.put("hunter2", marked: "org.nspasteboard.ConcealedType")
    r.settle()
    check(!bubble.isVisible, "a password-manager copy doesn't show it")
    Prefs.set("text", false)
    r.put("switched off")
    r.settle()
    Prefs.set("text", true)
    check(!bubble.isVisible, "a copy Settings says not to save doesn't show it")
    r.d.clipboardChanged(copied: false)
    pump(0.1)
    check(!bubble.isVisible, "undoing a paste-through doesn't show it")
    r.model.unseen = 0
    r.d.transfersChanged(["t:x": Transfer(done: 0, total: 1, peer: "Peer", sending: true)])
    r.d.transfersChanged([:])
    check(r.model.unseen == 0 && !bubble.isVisible, "a transfer finishing isn't a new item")

    // The shortcut.
    r.d.toggleBubble()
    check(until { r.opaque }, "the shortcut shows it")
    r.d.toggleBubble()
    check(until(1.3) { !bubble.isVisible }, "and hides it at once")
    r.d.toggleBubble()
    check(until { r.opaque }, "shown again")
    r.d.toggleBubble()
    pump(0.1)
    r.d.toggleBubble()
    check(until(1) { r.opaque }, "pressed during the fade, it shows again")
    pump(0.4)
    check(bubble.isVisible, "and the old fade doesn't finish the job")
    check(until { !bubble.isVisible }, "then it hides by itself")

    // A copy from another device, as the engine hands it over.
    r.d.connectSync()
    SyncEngine.shared.apply(r.row("first"))
    check(until { r.opaque }, "a copy from another device shows it, solid")
    check(r.board.string(forType: .string) == "first", "with the copy on the clipboard")
    check(until { !bubble.isVisible }, "and it hides again")
    let write = r.d.putOnClipboard
    r.d.putOnClipboard = { _ in nil }
    SyncEngine.shared.apply(r.row("first"))
    r.settle()
    r.d.putOnClipboard = write
    check(!bubble.isVisible, "one that couldn't reach the clipboard doesn't show it")

    // And from a real linked device: history stays quiet, a fresh copy doesn't.
    let live = "copied on the other device"
    let peerHome = testHome.appendingPathComponent("bubble-peer")
    try? FileManager.default.createDirectory(at: peerHome, withIntermediateDirectories: true)
    let peer = linkPeer(home: peerHome, ["SEED_TEXT": "older on the other device", "LIVE_TEXT": live])
    check(until { r.history.contains("older on the other device") }, "history arrives from a linked device")
    check(!bubble.isVisible, "and doesn't show it")
    check(until { r.opaque }, "a fresh copy there does")
    check(r.board.string(forType: .string) == live, "and it's on this clipboard, got \(r.board.string(forType: .string) ?? "nothing")")
    check(until { !bubble.isVisible }, "then it hides again")
    unlinkPeer(peer)

    // Whatever's in use keeps it.
    r.d.reveal()
    r.view.onHover(true)
    pump(2)
    check(bubble.isVisible, "hovering keeps it")
    r.view.onHover(false)
    check(until { !bubble.isVisible }, "it goes once the pointer leaves")

    r.d.reveal()
    r.view.onPress(true)
    pump(2)
    check(bubble.isVisible, "a press keeps it, even on the rim, where there's no hover")
    r.view.onPress(false)
    r.view.onClick()
    check(until { r.panel.isVisible }, "a click still opens the panel")
    r.d.toggle()
    check(until { !bubble.isVisible }, "and closing it starts the countdown")

    r.d.reveal()
    r.view.onDragStart()
    bubble.setFrameOrigin(NSPoint(x: stage[1].frame.maxX - 20, y: stage[1].frame.midY + 100))   // its center is past the edge
    pump(2)
    check(bubble.isVisible, "dragging keeps it")
    r.view.onDragEnd()
    check(until { !bubble.isVisible }, "and it goes after the drop")
    check(UserDefaults.standard.object(forKey: "cornerDisplay") as? Int == stage[1].id && UserDefaults.standard.string(forKey: "corner") == "tr",
          "its display is saved with its corner")
    let moved = Rig()
    check(moved.bubble.frame.maxX == stage[1].visible.maxX - 10 && moved.bubble.frame.maxY == stage[1].visible.maxY - 10,
          "after a restart it's back in that corner of that display")
    moved.close()

    // Hidden, it has no place to jump from, so it comes back where the pointer is.
    r.d.reveal()
    check(stage[0].visible.contains(bubble.frame), "shown again on the display with the pointer")
    check(until { !bubble.isVisible }, "gone")
    r.pointer = NSPoint(x: stage[1].frame.midX, y: stage[1].frame.midY)
    r.d.reveal()
    check(stage[1].visible.contains(bubble.frame), "and it follows the pointer")
    check(until { !bubble.isVisible }, "gone")
    r.pointer = NSPoint(x: stage[0].frame.midX, y: stage[0].frame.midY)

    // The panel brings it back from any route.
    r.d.toggle()
    check(r.opaque && r.panel.isVisible, "opening the panel shows it")
    pump(1)
    check(bubble.isVisible, "and it stays while the panel is open")
    r.d.toggle()
    r.d.toggle()
    pump(0.1)
    r.d.toggle()
    check(until { !bubble.isVisible }, "gone after the panel closes")
    r.d.reveal()
    pump(1.45)
    let panelDuringFade = until(1) { bubble.alphaValue < 0.35 }
    r.d.toggle()
    pump(0.5)
    check(panelDuringFade && r.opaque, "the panel opening during the fade leaves it solid")
    r.d.toggle()
    check(until { !bubble.isVisible }, "gone again")

    // Closing the panel ends recording, or both shortcuts stay off.
    r.d.toggle()
    r.model.recordingShortcut = true
    r.d.toggle()
    check(!r.model.recordingShortcut, "closing the panel ends recording")

    // Off again.
    r.model.hideBubble = false
    check(until { bubble.isVisible && abs(bubble.alphaValue - 0.4) < 0.05 }, "switching it off brings the bubble back, dimmed")
    r.settle()
    check(bubble.isVisible, "and it stays")

    placementTests()
    screenshotTests(r)
    pickTests(r)
    check(!strayed, "no window reached a real display, and the app never took focus")
}

/// Where the bubble starts: nothing here can show it on a real display, so these run with two.
private func placementTests() {
    func launched() -> NSRect {
        let r = Rig()
        defer { r.close() }
        return r.bubble.frame
    }
    UserDefaults.standard.set("tl", forKey: "corner")
    UserDefaults.standard.set(stage[1].id, forKey: "cornerDisplay")
    let saved = launched()
    check(saved.minX == stage[1].visible.minX + 10 && saved.maxY == stage[1].visible.maxY - 10, "the bubble starts in its saved corner of its saved display")
    UserDefaults.standard.set(999, forKey: "cornerDisplay")
    check(stage[0].visible.contains(launched()), "a display that's gone falls back to the first")
    UserDefaults.standard.removeObject(forKey: "cornerDisplay")
    check(stage[0].visible.contains(launched()), "and so does having none saved, as for everyone upgrading")
    UserDefaults.standard.removeObject(forKey: "corner")
}

/// A new screenshot always counts once, whichever way the two switches are set.
private func screenshotTests(_ r: Rig) {
    let bubble = r.bubble
    func take(_ name: String) -> (news: Int, hops: Int, copied: Bool) {
        let url = testHome.appendingPathComponent(name)
        FileManager.default.createFile(atPath: url.path, contents: Data(name.utf8))
        let hops = r.model.pulseTicks
        r.model.unseen = 0
        r.d.screenshotAdded(url, at: Date())
        r.settle()
        let copied = r.board.readObjects(forClasses: [NSURL.self], options: nil) as? [URL] == [url]
        return (r.model.unseen, r.model.pulseTicks - hops, copied)
    }
    r.model.hideBubble = true
    check(until { !bubble.isVisible }, "hidden for the screenshots")

    Prefs.set("files", false)
    let notSaved = take("copied but not saved.png")
    Prefs.set("files", true)
    check(notSaved == (1, 1, true), "with Copied files off, a screenshot still hops and counts once, got \(notSaved)")
    check(!bubble.isVisible, "but a copy Settings says not to save doesn't show it")

    UserDefaults.standard.set(false, forKey: "copyScreenshots")
    let notCopied = take("not copied.png")
    UserDefaults.standard.set(true, forKey: "copyScreenshots")
    check(notCopied == (1, 1, false), "with Also copy off, it hops, counts once and leaves the clipboard alone, got \(notCopied)")
    check(!bubble.isVisible, "and a screenshot that isn't copied doesn't show it")

    let both = take("both on.png")
    check(both == (1, 1, true), "with both on, capture announces it, once, got \(both)")
    check(until { !bubble.isVisible }, "gone again")
    r.model.hideBubble = false
    check(until { bubble.isVisible }, "back for the rest")
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
