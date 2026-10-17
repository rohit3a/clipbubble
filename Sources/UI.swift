import AppKit
import Carbon.HIToolbox
import ServiceManagement
import SwiftUI
import UniformTypeIdentifiers

// MARK: - Model

final class Model: ObservableObject {
    static let filters: [(tag: String, title: String)] =
        [("all", "All"), ("text", "Text"), ("image", "Images"), ("file", "Files"), ("shots", "Screenshots"),
         ("downloads", "Downloads"), ("iphone", "iPhone")]

    @Published var clips: [Clip] = []
    @Published var recent: [Clip] = []
    @Published var query = "" { didSet { if query != oldValue { reload() } } }
    @Published var filter = "all" { didSet { if filter != oldValue { reload() } } }
    @Published var selection: Int64?
    @Published var previewing = false
    @Published var showSettings = false
    @Published var prefsTick = 0
    @Published var shown = 0          // bumps on every open: refocuses search and refreshes relative times
    @Published var pulse = false
    @Published var pulseTicks = 0
    @Published var unseen = 0
    @Published var pressed = false
    @Published var hoverTicks = 0
    @Published var bubbleHover = false {
        didSet { if bubbleHover && !oldValue { hoverTicks += 1 }; if bubbleHover != oldValue { hoverChanged() } }
    }
    // Only real changes reach hoverChanged: hidePeek clears this, and a repeat would schedule hidePeek again, forever.
    @Published var peekHover = false { didSet { if peekHover != oldValue { hoverChanged() } } }
    @Published var links: [String: LinkState] = [:]
    @Published var syncPaused = SyncEngine.paused
    @Published var devices: [String: String] = [:]
    @Published var transfers: [String: Transfer] = [:]
    @Published var linked: [Hello] = []
    @Published var mascot = Mascots.active()
    @Published var mascots = Mascots.all()
    let db = DB()
    var pick: (Clip, _ paste: Bool) -> Void = { _, _ in }
    var dismiss: () -> Void = {}
    var hoverChanged: () -> Void = {}
    var prefsChanged: () -> Void = {}

    var selected: Clip? { clips.first { $0.id == selection } ?? clips.first }

    func reload() {
        let kind = ["text", "image", "file"].contains(filter) ? filter : nil
        let source = ["iphone": "iPhone", "shots": "Screenshot", "downloads": "Downloads"][filter]
        clips = db.clips(kind: kind, source: source, query: query)
        if clips.contains(where: { !$0.origin.isEmpty }) { devices = db.deviceNames() }
        if !clips.contains(where: { $0.id == selection }) { selection = clips.first?.id }
        Thumbs.prewarm(clips.prefix(40))
    }

    func reloadRecent() {
        recent = db.clips(limit: 3)
        Thumbs.prewarm(recent)
    }

    func setPref(_ key: String, _ on: Bool) {
        Prefs.set(key, on)
        prefsTick += 1
        reload()
        reloadRecent()
        prefsChanged()
    }

    func loadMascots() {
        AnimatedImage.clearCache()
        mascots = Mascots.all()
        mascot = Mascots.active()
    }

    func move(_ delta: Int) {
        guard !clips.isEmpty else { return }
        let i = clips.firstIndex { $0.id == selection } ?? 0
        selection = clips[min(max(i + delta, 0), clips.count - 1)].id
    }

    func cycleFilter(_ delta: Int) {
        let i = Self.filters.firstIndex { $0.tag == filter } ?? 0
        filter = Self.filters[(i + delta + Self.filters.count) % Self.filters.count].tag
    }

    func delete(_ c: Clip) {
        let i = clips.firstIndex { $0.id == c.id } ?? 0
        db.delete(id: c.id)
        clips.removeAll { $0.id == c.id }
        if selection == c.id { selection = clips.isEmpty ? nil : clips[min(i, clips.count - 1)].id }
        reloadRecent()
    }
}

// MARK: - Thumbnails

/// Decodes small thumbnails off the main thread and keeps them in memory, so rows never flash.
enum Thumbs {
    private static let cache = NSCache<NSString, NSImage>()
    private static var dims: [String: CGSize] = [:]   // main thread only
    private static let queue = DispatchQueue(label: "clipbubble.thumbs", qos: .userInitiated, attributes: .concurrent)

    private static func key(_ path: String, _ px: Int) -> NSString { "\(px)|\(path)" as NSString }
    static func cached(_ path: String, _ px: Int) -> NSImage? { cache.object(forKey: key(path, px)) }
    static func size(of path: String) -> CGSize? { dims[path] }

    static func load(_ path: String, _ px: Int, done: @escaping (NSImage?) -> Void) {
        if let hit = cached(path, px) { done(hit); return }
        queue.async {
            let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil)
            let props = src.flatMap { CGImageSourceCopyPropertiesAtIndex($0, 0, nil) as? [CFString: Any] }
            let opts = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: px,
                        kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary
            let img = src.flatMap { CGImageSourceCreateThumbnailAtIndex($0, 0, opts) }.map { NSImage(cgImage: $0, size: .zero) }
            if let img { cache.setObject(img, forKey: key(path, px)) }
            let w = props?[kCGImagePropertyPixelWidth] as? Int, h = props?[kCGImagePropertyPixelHeight] as? Int
            DispatchQueue.main.async {
                if let w, let h { dims[path] = CGSize(width: w, height: h) }
                done(img)
            }
        }
    }

    static func prewarm<S: Sequence>(_ clips: S) where S.Element == Clip {
        for c in clips { if let p = c.imagePath, cached(p, 64) == nil { load(p, 64) { _ in } } }
    }
}

struct Thumb: View {
    let path: String
    let px: Int
    var fit = false
    @State private var image: NSImage?

    init(path: String, px: Int, fit: Bool = false) {
        self.path = path
        self.px = px
        self.fit = fit
        _image = State(initialValue: Thumbs.cached(path, px))
    }

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().interpolation(.high).aspectRatio(contentMode: fit ? .fit : .fill)
            } else {
                Color.white.opacity(0.06)
            }
        }
        .onAppear { if image == nil { Thumbs.load(path, px) { image = $0 } } }
    }
}

// MARK: - Helpers

func ago(_ d: Date) -> String {
    let s = Int(-d.timeIntervalSinceNow)
    if s < 60 { return "now" }
    if s < 3600 { return "\(s / 60)m" }
    if s < 86400 { return "\(s / 3600)h" }
    return "\(s / 86400)d"
}

/// `#RRGGBB` or `RRGGBB` → a color.
func hexColor(_ s: String) -> Color? {
    var t = s.trimmingCharacters(in: .whitespacesAndNewlines)
    if t.hasPrefix("#") { t.removeFirst() }
    guard t.count == 6, let v = UInt32(t, radix: 16) else { return nil }
    return Color(red: Double(v >> 16 & 0xFF) / 255, green: Double(v >> 8 & 0xFF) / 255, blue: Double(v & 0xFF) / 255)
}

func isURL(_ s: String) -> Bool {
    let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
    return (t.hasPrefix("http://") || t.hasPrefix("https://")) && !t.contains(where: \.isWhitespace)
}

private var appIcons: [String: NSImage] = [:]
func appIcon(_ name: String) -> NSImage? {
    if let hit = appIcons[name] { return hit }
    let img = NSWorkspace.shared.runningApplications.first { $0.localizedName == name }?.icon
    if let img { appIcons[name] = img }
    return img
}

/// Hosting view that takes the first click even when its window isn't key.
final class ClickThroughHostingView<V: View>: NSHostingView<V> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

// MARK: - Windows

final class FloatingPanel: NSPanel {
    private let keyable: Bool

    init(size: NSSize, keyable: Bool) {
        self.keyable = keyable
        super.init(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = false
        appearance = NSAppearance(named: .darkAqua)
        animationBehavior = .none
    }

    override var canBecomeKey: Bool { keyable }
}

/// Dark frosted glass, masked to a rounded rectangle (a circle when radius is half the size).
func glass(frame: NSRect, radius: CGFloat) -> NSVisualEffectView {
    let v = NSVisualEffectView(frame: frame)
    v.material = .hudWindow
    v.blendingMode = .behindWindow
    v.state = .active
    v.autoresizingMask = [.width, .height]
    let side = radius * 2 + 1
    let mask = NSImage(size: NSSize(width: side, height: side), flipped: false) { r in
        NSColor.black.setFill()
        NSBezierPath(roundedRect: r, xRadius: radius, yRadius: radius).fill()
        return true
    }
    mask.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
    mask.resizingMode = .stretch
    v.maskImage = mask
    return v
}

/// Click toggles the panel; drag moves the bubble, and releasing snaps it to the nearest corner.
final class BubbleView: NSView {
    var onClick: () -> Void = {}
    var onPress: (Bool) -> Void = { _ in }
    var onDragStart: () -> Void = {}
    var onDragEnd: () -> Void = {}
    var onHover: (Bool) -> Void = { _ in }
    private var downAt = NSPoint.zero
    private var startOrigin = NSPoint.zero
    private var moved = false

    override func hitTest(_ p: NSPoint) -> NSView? { frame.contains(p) ? self : nil }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityRole() -> NSAccessibility.Role? { .button }
    override func accessibilityLabel() -> String? { "ClipBubble" }
    override func accessibilityHelp() -> String? { "Opens your clipboard history" }
    override func accessibilityPerformPress() -> Bool { onClick(); return true }

    override func updateTrackingAreas() {
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds.insetBy(dx: 9, dy: 9), options: [.mouseEnteredAndExited, .activeAlways],
                                       owner: self))
    }
    override func mouseEntered(with e: NSEvent) { onHover(true) }
    override func mouseExited(with e: NSEvent) { onHover(false) }

    override func mouseDown(with e: NSEvent) {
        downAt = NSEvent.mouseLocation
        startOrigin = window?.frame.origin ?? .zero
        moved = false
        onPress(true)
    }

    override func mouseDragged(with e: NSEvent) {
        let p = NSEvent.mouseLocation
        if !moved {
            guard hypot(p.x - downAt.x, p.y - downAt.y) > 3 else { return }
            moved = true
            onDragStart()
        }
        window?.setFrameOrigin(NSPoint(x: startOrigin.x + p.x - downAt.x, y: startOrigin.y + p.y - downAt.y))
    }

    override func mouseUp(with e: NSEvent) {
        onPress(false)
        moved ? onDragEnd() : onClick()
    }
}

// MARK: - Bubble face

struct BubbleFace: View {
    @ObservedObject var model: Model
    @State private var ringScale: CGFloat = 1
    @State private var ringOpacity = 0.0

    var body: some View {
        ZStack {
            Circle().stroke(Color.accentColor, lineWidth: 2).scaleEffect(ringScale).opacity(ringOpacity)
            Circle().fill(LinearGradient(colors: [.white.opacity(0.16), .clear], startPoint: .top, endPoint: .center))
            Circle().strokeBorder(LinearGradient(colors: [.white.opacity(0.45), .white.opacity(0.08)],
                                                 startPoint: .top, endPoint: .bottom), lineWidth: 1)
            MascotView(mascot: model.mascot, hover: model.bubbleHover, pressed: model.pressed, happy: model.pulse,
                       hoverTicks: model.hoverTicks, happyTicks: model.pulseTicks)
        }
        .frame(width: 46, height: 46)
        .overlay(alignment: .topTrailing) {
            if model.unseen > 0 {
                Text("\(min(model.unseen, 99))")
                    .font(.system(size: 10, weight: .bold, design: .rounded)).foregroundStyle(.white)
                    .padding(.horizontal, 5).frame(minWidth: 18, minHeight: 18)
                    .background(Capsule().fill(Color.accentColor))
                    .overlay(Capsule().strokeBorder(.white.opacity(0.35)))
                    .offset(x: 5, y: -5)
                    .transition(.scale.combined(with: .opacity))
            }
        }
        .overlay(alignment: .bottomLeading) {
            if model.syncPaused && !SyncEngine.enabledHosts.isEmpty {
                Image(systemName: "pause.circle.fill").font(.system(size: 13)).foregroundStyle(.white, Color.orange)
                    .offset(x: -4, y: 4).help("Sync is paused")
            }
        }
        .scaleEffect(model.bubbleHover ? 1.06 : 1)
        .animation(.spring(response: 0.2, dampingFraction: 0.7), value: model.bubbleHover)
        .animation(.spring(response: 0.25, dampingFraction: 0.7), value: model.unseen)
        .frame(width: 64, height: 64)
        .onChange(of: model.pulseTicks) {
            ringScale = 1
            ringOpacity = 0.8
            withAnimation(.easeOut(duration: 0.7)) { ringScale = 1.55; ringOpacity = 0 }
        }
    }
}

// MARK: - Rows

struct Row: View, Equatable {
    let clip: Clip
    let index: Int          // ⌘1–9 hint for the first nine; pass -1 to hide
    let selected: Bool
    let tick: Int
    let transfer: Transfer?
    let model: Model        // not observed, so a row redraws only when its own inputs change
    @State private var hover = false

    static func == (a: Row, b: Row) -> Bool {
        a.clip == b.clip && a.index == b.index && a.selected == b.selected && a.tick == b.tick && a.transfer == b.transfer
    }

    var body: some View {
        HStack(spacing: 10) {
            Tile(clip: clip)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .medium)).lineLimit(1)
                HStack(spacing: 4) {
                    if !clip.origin.isEmpty {
                        Image(systemName: clip.remote ? "icloud.and.arrow.down" : "desktopcomputer").font(.system(size: 9.5))
                    } else if clip.source == "iPhone" {
                        Image(systemName: "iphone").font(.system(size: 9.5))
                    } else if clip.source == "Screenshot" {
                        Image(systemName: "camera.viewfinder").font(.system(size: 9.5))
                    } else if clip.source == "Downloads" {
                        Image(systemName: "arrow.down.circle").font(.system(size: 9.5))
                    } else if let icon = appIcon(clip.source) {
                        Image(nsImage: icon).resizable().frame(width: 12, height: 12)
                    }
                    Text(subtitle).lineLimit(1)
                }
                .font(.system(size: 11)).foregroundStyle(.secondary)
            }
            Spacer(minLength: 6)
            if let t = transfer {
                ProgressView(value: t.fraction).progressViewStyle(.linear).frame(width: 44).controlSize(.mini)
                Button { SyncEngine.shared.cancelTransfer(clip.hash) } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 12)).foregroundStyle(.secondary)
                }
                .buttonStyle(.plain).help("Stop")
            } else if clip.remote {
                Button { SyncEngine.shared.get(clip.hash) } label: {
                    Label("Get", systemImage: "arrow.down.circle").font(.system(size: 11, weight: .medium))
                }
                .buttonStyle(.borderless).help("Download it from \(model.devices[clip.origin] ?? "the other device")")
            }
            if index >= 0 && index < 9 {
                Text("⌘\(index + 1)").font(.system(size: 11, weight: .medium, design: .rounded))
                    .foregroundStyle(selected ? .secondary : .tertiary)
            }
        }
        .padding(.horizontal, 8).frame(height: 46)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous)
            .fill(Color.white.opacity(selected ? 0.12 : hover ? 0.06 : 0)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture { model.pick(clip, !NSEvent.modifierFlags.contains(.option)) }
        .onDrag { dragItem }
        .contextMenu {
            Button("Paste") { model.pick(clip, true) }
            Button("Copy") { model.pick(clip, false) }
            if let p = clip.imagePath ?? clip.paths.first {
                Button("Open") { NSWorkspace.shared.open(URL(fileURLWithPath: p)) }
                Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: p)]) }
            }
            if clip.remote && transfer == nil {
                Button("Get from \(model.devices[clip.origin] ?? "other device")") { SyncEngine.shared.get(clip.hash) }
            } else if clip.kind != "text", !clip.remote, !model.linked.isEmpty {
                Menu("Send to") {
                    ForEach(model.linked, id: \.device) { h in
                        Button(h.name) { SyncEngine.shared.send(clip.hash, toDevice: h.device) }
                    }
                }
            }
            if transfer != nil { Button("Stop transfer") { SyncEngine.shared.cancelTransfer(clip.hash) } }
            Divider()
            Button("Delete") { model.delete(clip) }
        }
    }

    /// Only the start of the text matters for a row, so long copies don't slow the list.
    private var head: Substring { clip.text.prefix(20_000) }

    private var firstLine: String {
        head.split(whereSeparator: \.isNewline).first { !$0.allSatisfy(\.isWhitespace) }
            .map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
    }

    private var title: String {
        switch clip.kind {
        case "image":
            guard let p = clip.imagePath, let s = Thumbs.size(of: p) else { return "Image" }
            return "Image  \(Int(s.width))×\(Int(s.height))"
        case "file": return clip.paths.map { ($0 as NSString).lastPathComponent }.joined(separator: ", ")
        default:
            let t = String(firstLine.prefix(200))
            return isURL(t) ? t.replacingOccurrences(of: "https://", with: "").replacingOccurrences(of: "http://", with: "") : t
        }
    }

    private var subtitle: String {
        if let t = transfer {
            let f = ByteCountFormatter()
            return "\(t.sending ? "Sending to" : "Getting from") \(t.peer) · \(f.string(fromByteCount: t.done)) of \(f.string(fromByteCount: t.total))"
        }
        var parts = [clip.source, ago(clip.created)]
        if !clip.origin.isEmpty { parts.insert(model.devices[clip.origin] ?? "Other device", at: 0) }
        if clip.remote {
            parts.append("not downloaded yet")
        } else if clip.kind == "file", let p = clip.paths.first {
            parts.append(((p as NSString).deletingLastPathComponent as NSString).abbreviatingWithTildeInPath)
        } else if clip.kind == "text" {
            let lines = head.split(whereSeparator: \.isNewline).count
            if lines > 1 { parts.append(clip.text.count > head.count ? "\(lines)+ lines" : "\(lines) lines") }
        }
        return parts.joined(separator: " · ")
    }

    private var dragItem: NSItemProvider {
        if let p = clip.imagePath ?? clip.paths.first {
            // A file that's gone drags nothing rather than an empty string.
            return NSItemProvider(contentsOf: URL(fileURLWithPath: p)) ?? NSItemProvider()
        }
        return NSItemProvider(object: clip.text as NSString)
    }
}

struct Tile: View {
    let clip: Clip

    var body: some View {
        Group {
            if let p = clip.imagePath {
                Thumb(path: p, px: 64)
            } else if let p = clip.paths.first {
                Image(nsImage: NSWorkspace.shared.icon(forFile: p)).resizable().padding(2)
            } else if let c = hexColor(clip.text) {
                c
            } else if isURL(clip.text) {
                symbol("link", Color.blue)
            } else {
                symbol("text.alignleft", Color.white)
            }
        }
        .frame(width: 30, height: 30)
        .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous).strokeBorder(Color.white.opacity(0.08)))
    }

    private func symbol(_ name: String, _ tint: Color) -> some View {
        Image(systemName: name).font(.system(size: 12, weight: .semibold)).foregroundStyle(tint.opacity(0.85))
            .frame(maxWidth: .infinity, maxHeight: .infinity).background(tint.opacity(0.12))
    }
}

// MARK: - Panel

struct PanelView: View {
    @ObservedObject var model: Model
    @FocusState private var searchFocused: Bool
    @State private var lift: CGFloat = 0

    var body: some View {
        Group {
            if model.showSettings { SettingsView(model: model) } else { main }
        }
        .frame(width: 420, height: 470)
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Color.white.opacity(0.1)))
        .offset(y: lift)
        .onChange(of: model.shown) {
            searchFocused = true
            lift = 6
            withAnimation(.spring(response: 0.2, dampingFraction: 0.9)) { lift = 0 }
        }
    }

    private var main: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "magnifyingglass").font(.system(size: 15, weight: .medium)).foregroundStyle(.secondary)
                TextField("Search clipboard…", text: $model.query)
                    .textFieldStyle(.plain).font(.system(size: 16)).focused($searchFocused)
                if !model.query.isEmpty {
                    Button { model.query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                        .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 16).frame(height: 50)

            HStack(spacing: 2) {
                ForEach(Model.filters, id: \.tag) { f in
                    let on = model.filter == f.tag
                    Button { model.filter = f.tag } label: {
                        Text(f.title).font(.system(size: 11.5, weight: .medium)).fixedSize()
                            .padding(.horizontal, 7).padding(.vertical, 4)
                            .background(Capsule().fill(Color.white.opacity(on ? 0.14 : 0)))
                            .foregroundStyle(on ? Color.primary : Color.secondary)
                            .contentShape(Capsule())
                    }
                    .buttonStyle(.plain)
                }
                Spacer()
                Text("tab").font(.system(size: 10, weight: .medium, design: .rounded)).foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 12).padding(.bottom, 8)

            Rectangle().fill(Color.white.opacity(0.07)).frame(height: 1)

            ZStack {
                list
                if model.previewing, let c = model.selected { PreviewPane(clip: c) }
            }
            .frame(maxHeight: .infinity)

            Rectangle().fill(Color.white.opacity(0.07)).frame(height: 1)
            footer
        }
    }

    @ViewBuilder private var list: some View {
        if model.clips.isEmpty {
            VStack(spacing: 8) {
                Image(systemName: "doc.on.clipboard").font(.system(size: 26, weight: .light)).foregroundStyle(.tertiary)
                Text(model.query.isEmpty ? "Copy anything — it lands here" : "No matches")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            }
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 1) {
                        ForEach(Array(model.clips.enumerated()), id: \.element.id) { i, c in
                            Row(clip: c, index: i, selected: c.id == model.selection, tick: model.shown,
                                transfer: model.transfers[c.hash], model: model)
                                .equatable().id(c.id)
                        }
                    }
                    .padding(6)
                }
                .scrollIndicators(.never)
                .onChange(of: model.selection) { _, id in if let id { proxy.scrollTo(id) } }
                .onChange(of: model.shown) { if let id = model.clips.first?.id { proxy.scrollTo(id, anchor: .top) } }
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 14) {
            Button { model.showSettings = true } label: {
                Image(systemName: "gearshape.fill").font(.system(size: 12)).foregroundStyle(.secondary)
            }
            .buttonStyle(.plain).help("Settings")
            Spacer()
            Key(keys: "↩", label: "Paste")
            Key(keys: "⌥↩", label: "Copy")
            Key(keys: "⌘Y", label: "Preview")
            Key(keys: "⌘⌫", label: "Delete")
        }
        .padding(.horizontal, 14).frame(height: 36)
    }

}

/// Settings, shown in place of the list (⚙ in the footer, Esc to go back).
struct SettingsView: View {
    @ObservedObject var model: Model
    @State private var copyShots = UserDefaults.standard.bool(forKey: "copyScreenshots")
    @State private var openAtLogin = SMAppService.mainApp.status == .enabled
    @State private var promptCopied = false
    @State private var autoPaste = UserDefaults.standard.object(forKey: "autoPaste") as? Bool ?? true
    @State private var shotsOK: Bool?
    @State private var dlOK: Bool?
    @State private var axOK: Bool?
    private let ticker = Timer.publish(every: 2, on: .main, in: .common).autoconnect()
    private var customShots: Bool { _ = model.prefsTick; return !(UserDefaults.standard.string(forKey: "screenshotFolder") ?? "").isEmpty }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Button { model.showSettings = false } label: {
                    HStack(spacing: 6) {
                        Image(systemName: "chevron.left").font(.system(size: 12, weight: .semibold)).foregroundStyle(.secondary)
                        Text("Settings").font(.system(size: 16, weight: .semibold))
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                Spacer()
                Text("esc").font(.system(size: 10, weight: .medium, design: .rounded)).foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 16).frame(height: 50)
            Rectangle().fill(Color.white.opacity(0.07)).frame(height: 1)

            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    section("Save to history") {
                        ForEach(Prefs.categories.filter { !["screenshots", "downloads"].contains($0.key) }, id: \.key) { c in
                            toggleRow(c.icon, c.title, c.detail, isOn: Binding(
                                get: { _ = model.prefsTick; return Prefs.on(c.key) },
                                set: { model.setPref(c.key, $0) }))
                        }
                        Text("Turning one off stops saving it and hides what's already there, for your agents too.")
                            .font(.system(size: 10.5)).foregroundStyle(.tertiary).padding(.top, 2)
                    }
                    section("Folders & permissions") {
                        permRow("camera.viewfinder", "Screenshots", folder: FolderWatcher.screenshotFolder,
                                note: customShots ? "Folder you picked" : "From your macOS screenshot setting",
                                ok: shotsOK, isOn: pref("screenshots"), fix: openFilesSettings) {
                            HStack(spacing: 12) {
                                Button("Change folder…", action: pickShotsFolder)
                                if customShots { Button("Use macOS setting") { UserDefaults.standard.removeObject(forKey: "screenshotFolder"); model.setPref("screenshots", true) } }
                            }
                            .buttonStyle(.link).font(.system(size: 11))
                            Toggle("Also copy new ones to the clipboard", isOn: $copyShots)
                                .toggleStyle(.checkbox).font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                        permRow("arrow.down.circle", "Recent downloads", folder: FolderWatcher.downloadsFolder, note: nil,
                                ok: dlOK, isOn: pref("downloads"), fix: openFilesSettings) { EmptyView() }
                        permRow("keyboard", "Auto-paste", folder: nil, note: "↩ pastes into the app you were in. Needs Accessibility access.",
                                ok: autoPaste ? axOK : nil, isOn: $autoPaste, fix: openAccessibilitySettings) { EmptyView() }
                        Text("macOS doesn't let apps change these themselves. Switching one on asks for access; if it was blocked, Fix opens the right page in System Settings.")
                            .font(.system(size: 10.5)).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true).padding(.top, 2)
                    }
                    section("Sync over SSH") {
                        SyncSettings(model: model)
                    }
                    section("Mascot") {
                        LazyVGrid(columns: Array(repeating: GridItem(.fixed(44), spacing: 8), count: 7), alignment: .leading, spacing: 8) {
                            ForEach(model.mascots) { m in
                                ZStack {
                                    Circle().fill(Color(white: 0.09))
                                    MascotView(mascot: m, hover: false, pressed: false, happy: false, hoverTicks: 0, happyTicks: 0)
                                        .scaleEffect(0.8)
                                }
                                .frame(width: 42, height: 42)
                                .overlay(Circle().strokeBorder(m.id == model.mascot.id ? Color.accentColor : Color.white.opacity(0.1),
                                                               lineWidth: m.id == model.mascot.id ? 2 : 1))
                                .contentShape(Circle())
                                .onTapGesture { Mascots.setActive(m.id); model.loadMascots() }
                                .help(m.name)
                            }
                            Button(action: addMascot) {
                                Image(systemName: "plus").font(.system(size: 14, weight: .semibold)).foregroundStyle(.secondary)
                                    .frame(width: 42, height: 42)
                                    .overlay(Circle().strokeBorder(Color.white.opacity(0.18), style: StrokeStyle(lineWidth: 1, dash: [3, 3])))
                                    .contentShape(Circle())
                            }
                            .buttonStyle(.plain).help("Add your own (PNG, GIF, SVG)")
                        }
                    }
                    section("AI agents (MCP)") {
                        Text("Let Claude Code, Codex, Cursor and other agents search your history, see your screenshots, put things on your clipboard and design mascots.")
                            .font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                            .padding(.bottom, 6)
                        Button {
                            Board.writeText(MCP.setupPrompt(binary: Bundle.main.executablePath ?? ""), transient: true)
                            promptCopied = true
                            DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) { promptCopied = false }
                        } label: {
                            Label(promptCopied ? "Copied! Paste it into your agent" : "Copy setup prompt",
                                  systemImage: promptCopied ? "checkmark" : "doc.on.doc")
                        }
                        .controlSize(.small)
                        Text("Paste it into your agent and it connects itself.")
                            .font(.system(size: 10.5)).foregroundStyle(.tertiary).padding(.top, 4)
                    }
                    section("General") {
                        toggleRow("power", "Open at login", nil, isOn: $openAtLogin)
                        HStack {
                            Button("Clear all history…", action: clearAll)
                            Spacer()
                            Button("Quit ClipBubble") { NSApp.terminate(nil) }
                        }
                        .controlSize(.small).padding(.top, 6)
                    }
                }
                .padding(16)
            }
            .scrollIndicators(.never)
        }
        .onAppear(perform: refresh)
        .onReceive(ticker) { _ in refresh() }   // picks up changes made in System Settings
        .onChange(of: model.prefsTick) { refresh() }
        .onChange(of: autoPaste) { _, on in
            UserDefaults.standard.set(on, forKey: "autoPaste")
            if on && !AXIsProcessTrusted() {
                AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
            }
            refresh()
        }
        .onChange(of: copyShots) { _, on in UserDefaults.standard.set(on, forKey: "copyScreenshots") }
        .onChange(of: openAtLogin) { _, on in
            try? on ? SMAppService.mainApp.register() : SMAppService.mainApp.unregister()
        }
    }

    private func pref(_ key: String) -> Binding<Bool> {
        Binding(get: { _ = model.prefsTick; return Prefs.on(key) }, set: { model.setPref(key, $0) })
    }

    private func refresh() {
        shotsOK = Prefs.on("screenshots") ? FolderWatcher.canRead(FolderWatcher.screenshotFolder) : nil
        dlOK = Prefs.on("downloads") ? FolderWatcher.canRead(FolderWatcher.downloadsFolder) : nil
        axOK = AXIsProcessTrusted()
    }

    private func openFilesSettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders")!)
    }

    private func openAccessibilitySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }

    private func pickShotsFolder() {
        model.dismiss()
        NSApp.activate(ignoringOtherApps: true)
        let open = NSOpenPanel()
        open.canChooseDirectories = true
        open.canChooseFiles = false
        open.directoryURL = FolderWatcher.screenshotFolder
        open.message = "Pick the folder ClipBubble should watch for screenshots."
        guard open.runModal() == .OK, let url = open.url else { return }
        UserDefaults.standard.set(url.path, forKey: "screenshotFolder")
        model.setPref("screenshots", true)
    }

    /// A feature that needs a folder or permission: switch, where it looks, and whether macOS lets it.
    private func permRow<X: View>(_ icon: String, _ title: String, folder: URL?, note: String?, ok: Bool?,
                                  isOn: Binding<Bool>, fix: @escaping () -> Void, @ViewBuilder extra: () -> X) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon).font(.system(size: 13)).foregroundStyle(.secondary).frame(width: 20).padding(.top, 1)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(size: 13, weight: .medium))
                if let folder {
                    Text((folder.path as NSString).abbreviatingWithTildeInPath)
                        .font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                        .lineLimit(1).truncationMode(.middle)
                }
                if let note { Text(note).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
                HStack(spacing: 6) {
                    switch ok {
                    case .some(true):
                        Label("Allowed", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    case .some(false):
                        Label("No access", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        Button("Fix", action: fix).buttonStyle(.link)
                    case .none:
                        Label("Off", systemImage: "minus.circle").foregroundStyle(.tertiary)
                    }
                }
                .font(.system(size: 11, weight: .medium)).labelStyle(.titleAndIcon)
                extra()
            }
            Spacer(minLength: 8)
            Toggle("", isOn: isOn).toggleStyle(.switch).controlSize(.mini).labelsHidden()
        }
        .padding(.vertical, 6)
    }

    private func section<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title.uppercased()).font(.system(size: 10, weight: .semibold)).tracking(0.6).foregroundStyle(.tertiary)
                .padding(.bottom, 4)
            content()
        }
    }

    private func toggleRow(_ icon: String, _ title: String, _ detail: String?, isOn: Binding<Bool>) -> some View {
        row(icon, title, detail) {
            Toggle("", isOn: isOn).toggleStyle(.switch).controlSize(.mini).labelsHidden()
        }
    }

    private func row<T: View>(_ icon: String, _ title: String, _ detail: String?, @ViewBuilder trailing: () -> T) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon).font(.system(size: 13)).foregroundStyle(.secondary).frame(width: 20)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 13, weight: .medium))
                if let detail { Text(detail).font(.system(size: 11)).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 8)
            trailing()
        }
        .padding(.vertical, 5)
    }

    /// Picks an image or GIF and makes it the mascot.
    private func addMascot() {
        model.dismiss()
        NSApp.activate(ignoringOtherApps: true)
        let open = NSOpenPanel()
        open.allowedContentTypes = [.png, .gif, .jpeg, .svg, .heic, .webP]
        open.message = "Pick an image or GIF for your mascot. Square with a transparent background looks best."
        guard open.runModal() == .OK, let url = open.url else { return }
        do {
            let m = try Mascots.add(name: url.deletingPathExtension().lastPathComponent, kind: "image", states: ["idle": url.path])
            Mascots.setActive(m.id)
            model.loadMascots()
        } catch {
            NSAlert(error: error).runModal()
        }
    }

    private func clearAll() {
        let alert = NSAlert()
        alert.messageText = "Clear all clipboard history?"
        alert.informativeText = "This removes every saved item and image. It can't be undone."
        alert.addButton(withTitle: "Clear")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn { model.db.deleteAll(); model.reload(); model.reloadRecent() }
    }
}

/// Settings → Sync over SSH: one switch per host in ~/.ssh/config, with its live status.
struct SyncSettings: View {
    @ObservedObject var model: Model
    @State private var hosts: [String] = []
    @State private var enabled = Set(SyncEngine.enabledHosts)
    @State private var setup: [String: String] = [:]   // progress or result text per host
    @State private var installing: Set<String> = []

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Copy on one machine, paste on another. Works with ClipBubble on your other Macs, Linux and Windows machines, using your SSH config and keys.")
                .font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).padding(.bottom, 6)
            if hosts.isEmpty {
                Text("No hosts in ~/.ssh/config yet. Add one there and it shows up here.")
                    .font(.system(size: 11)).foregroundStyle(.tertiary)
            }
            ForEach(hosts, id: \.self) { host in
                HStack(spacing: 10) {
                    Image(systemName: "server.rack").font(.system(size: 13)).foregroundStyle(.secondary).frame(width: 20)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(host).font(.system(size: 13, weight: .medium))
                        if enabled.contains(host) {
                            TimelineView(.periodic(from: .now, by: 1)) { _ in
                                let state = model.syncPaused ? nil : model.links[host]
                                HStack(spacing: 5) {
                                    Circle().fill(state?.ok == true ? Color.green : state == nil ? Color.gray : Color.orange)
                                        .frame(width: 6, height: 6)
                                    Text(model.syncPaused ? "Paused" : state?.text ?? "Connecting…")
                                }
                                .font(.system(size: 11)).foregroundStyle(.secondary)
                            }
                            if let note = setup[host] {
                                Text(note).font(.system(size: 11)).foregroundStyle(.secondary)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                    Spacer(minLength: 8)
                    if enabled.contains(host), !model.syncPaused, let label = setupLabel(model.links[host]) {
                        Button(label) { install(host) }
                            .controlSize(.small).disabled(installing.contains(host))
                    }
                    Toggle("", isOn: Binding(get: { enabled.contains(host) }, set: { on in
                        if on { enabled.insert(host) } else { enabled.remove(host) }
                        SyncEngine.shared.setHost(host, on: on)
                    }))
                    .toggleStyle(.switch).controlSize(.mini).labelsHidden()
                }
                .padding(.vertical, 5)
            }
            if !enabled.isEmpty {
                HStack(spacing: 10) {
                    Image(systemName: "pause.circle").font(.system(size: 13)).foregroundStyle(.secondary).frame(width: 20)
                    Text("Pause sync").font(.system(size: 13, weight: .medium))
                    Spacer()
                    Toggle("", isOn: Binding(get: { model.syncPaused }, set: { model.syncPaused = $0; SyncEngine.shared.setPaused($0) }))
                        .toggleStyle(.switch).controlSize(.mini).labelsHidden()
                }
                .padding(.vertical, 5)
            }
            Text("This Mac shows up as \(SyncEngine.shared.name). Files up to 25 MB move on their own. Password-manager copies never leave this Mac.")
                .font(.system(size: 10.5)).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true).padding(.top, 2)
        }
        .onAppear { hosts = SSHConfig.hosts() }
    }

    private func setupLabel(_ state: LinkState?) -> String? {
        switch state {
        case .notInstalled: return "Set up"
        case .versionMismatch(let h): return h.os == "linux" ? "Update" : nil
        case .live(let h): return RemoteSetup.isOutdated(h) ? "Update" : nil
        default: return nil
        }
    }

    private func install(_ host: String) {
        installing.insert(host)
        RemoteSetup.run(host, progress: { setup[host] = $0 }, done: { r in
            installing.remove(host)
            setup[host] = r.text
            if case .otherOS = r { NSWorkspace.shared.open(RemoteSetup.releases) }
            SyncEngine.shared.redial(host)
        })
    }
}

struct Key: View {
    let keys: String, label: String
    var body: some View {
        HStack(spacing: 5) {
            Text(label).font(.system(size: 11)).foregroundStyle(.secondary)
            Text(keys).font(.system(size: 10.5, weight: .semibold, design: .rounded)).foregroundStyle(.secondary)
                .padding(.horizontal, 5).padding(.vertical, 2)
                .background(RoundedRectangle(cornerRadius: 4, style: .continuous).fill(Color.white.opacity(0.09)))
        }
    }
}

/// ⌘Y: the selected item at full size.
struct PreviewPane: View {
    let clip: Clip

    var body: some View {
        Group {
            if let p = clip.imagePath {
                Thumb(path: p, px: 1400, fit: true).padding(12)
            } else if !clip.paths.isEmpty {
                VStack(spacing: 10) {
                    Image(nsImage: NSWorkspace.shared.icon(forFile: clip.paths[0])).resizable().frame(width: 64, height: 64)
                    Text(clip.paths.joined(separator: "\n")).font(.system(size: 11.5)).foregroundStyle(.secondary)
                        .multilineTextAlignment(.center).textSelection(.enabled)
                }
                .padding(20)
            } else {
                ScrollView {
                    // Laying out megabytes of text would freeze the panel; the full text still pastes.
                    let shown = clip.text.prefix(100_000)
                    Text(shown.count < clip.text.count ? shown + "\n\n… the rest is too long to preview, but it pastes in full" : shown)
                        .font(.system(size: 12, design: .monospaced)).textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(14)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.black.opacity(0.55))
        .background(.ultraThinMaterial)
    }
}

/// Hovering the bubble: the last three items, one click to paste.
struct PeekView: View {
    @ObservedObject var model: Model

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            HStack {
                Text("RECENT").font(.system(size: 10, weight: .semibold)).tracking(0.6).foregroundStyle(.tertiary)
                Spacer()
                Text("⌃⌥V for all").font(.system(size: 10)).foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 8).padding(.top, 4).padding(.bottom, 3)
            if model.recent.isEmpty {
                Text("Nothing copied yet").font(.system(size: 12)).foregroundStyle(.secondary).padding(8)
            }
            ForEach(model.recent) { Row(clip: $0, index: -1, selected: false, tick: model.shown, transfer: model.transfers[$0.hash], model: model) }
        }
        .padding(6)
        .frame(width: 300)
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.white.opacity(0.1)))
        .onHover { model.peekHover = $0 }
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate {
    private static let idleAlpha: CGFloat = 0.4
    private static let margin: CGFloat = 10
    let model = Model()
    private var bubble: FloatingPanel!
    private var panel: FloatingPanel!
    private var peek: FloatingPanel!
    private var peekHost: NSView!
    private var capture: Capture!
    private var screenshots: FolderWatcher?
    private var downloads: FolderWatcher?
    private var clickMonitor: Any?
    private var keyMonitor: Any?
    private var hotKey: EventHotKeyRef?
    private var pulseWork: DispatchWorkItem?
    private var peekWork: DispatchWorkItem?
    private var dragging = false
    private var askedForAccessibility = false

    func applicationDidFinishLaunching(_ n: Notification) {
        UserDefaults.standard.register(defaults: ["copyScreenshots": true, "corner": "br"])
        model.db.prune()
        model.reload()
        model.reloadRecent()
        installEditMenu()

        bubble = FloatingPanel(size: NSSize(width: 64, height: 64), keyable: false)
        let view = BubbleView(frame: NSRect(x: 0, y: 0, width: 64, height: 64))
        view.addSubview(glass(frame: NSRect(x: 9, y: 9, width: 46, height: 46), radius: 23))
        let face = NSHostingView(rootView: BubbleFace(model: model))
        face.frame = view.bounds
        view.addSubview(face)
        view.onClick = { [weak self] in self?.toggle() }
        view.onPress = { [weak self] in self?.model.pressed = $0 }
        view.onDragStart = { [weak self] in
            guard let self else { return }
            self.dragging = true
            self.hidePanel()
            self.hidePeek()
            self.bubble.alphaValue = 1
        }
        view.onDragEnd = { [weak self] in self?.dragging = false; self?.snap(animated: true) }
        view.onHover = { [weak self] in self?.model.bubbleHover = $0 }
        bubble.contentView = view
        snap(animated: false)
        bubble.alphaValue = Self.idleAlpha
        bubble.orderFrontRegardless()

        panel = FloatingPanel(size: NSSize(width: 420, height: 470), keyable: true)
        panel.contentView = hosted(PanelView(model: model), size: NSSize(width: 420, height: 470), radius: 16)

        peek = FloatingPanel(size: NSSize(width: 300, height: 100), keyable: false)
        peekHost = ClickThroughHostingView(rootView: PeekView(model: model))
        let peekGlass = glass(frame: NSRect(x: 0, y: 0, width: 300, height: 100), radius: 14)
        peekHost.frame = peekGlass.bounds
        peekHost.autoresizingMask = [.width, .height]
        peekGlass.addSubview(peekHost)
        peek.contentView = peekGlass

        model.pick = { [weak self] c, paste in self?.pick(c, paste: paste) }
        model.dismiss = { [weak self] in self?.hidePanel() }
        model.hoverChanged = { [weak self] in self?.updatePeek() }

        capture = Capture { [weak self] in DispatchQueue.main.async { self?.captured() } }
        syncScreenshots()
        syncDownloads()
        model.prefsChanged = { [weak self] in self?.syncScreenshots(); self?.syncDownloads(); self?.importFolders() }
        // Notice when the screenshot location changes in ⌘⇧5 → Options.
        Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in
            guard let self, self.screenshots?.folder != FolderWatcher.screenshotFolder else { return }
            self.syncScreenshots()
            self.importFolders()
        }
        importFolders()
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self] in self?.importFolders() }
        Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in
            self?.model.db.prune()
            self?.importFolders()
        }
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in self?.snap(animated: false) }
        // Mascot changes from the MCP server (another process) land here.
        DistributedNotificationCenter.default().addObserver(forName: Mascots.changed, object: nil, queue: .main) { [weak self] _ in
            self?.model.loadMascots()
            self?.model.pulseTicks += 1
        }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in self?.handleKey(e) ?? e }
        registerHotKey()
        let sync = SyncEngine.shared
        sync.onReceived = { [weak self] in self?.captured() }
        sync.onStatus = { [weak self] in self?.model.links = sync.statuses; self?.model.linked = sync.linked }
        sync.onTransfers = { [weak self] t in
            let finished = self?.model.transfers.keys.contains { t[$0] == nil } ?? false
            self?.model.transfers = t
            if finished { self?.captured() }
        }
        sync.start()
    }

    /// Every screenshot lands in history; copying it to the clipboard as well is optional.
    private func syncScreenshots() {
        let folder = FolderWatcher.screenshotFolder
        guard Prefs.on("screenshots") else { screenshots?.stop(); screenshots = nil; return }
        guard screenshots?.folder != folder else { return }
        screenshots?.stop()
        screenshots = FolderWatcher(folder: folder, maxAge: 120,
                                    include: { isImagePath($0.path) || ["mov", "mp4"].contains($0.pathExtension.lowercased()) }) { [weak self] url, date in
            self?.model.db.insertIfMissing(kind: "file", text: url.path, source: "Screenshot", hash: "f:" + sha(url.path), created: date)
            if UserDefaults.standard.bool(forKey: "copyScreenshots") { Board.writeFiles([url]) } else { self?.captured() }
        }
    }

    /// Watches ~/Downloads only while that's switched on (so the privacy prompt only shows up if you want it).
    private func syncDownloads() {
        if Prefs.on("downloads") {
            guard downloads == nil, let dir = FolderWatcher.downloadsFolder else { return }
            downloads = FolderWatcher(folder: dir, maxAge: nil,
                                      include: { !FolderWatcher.tempExts.contains($0.pathExtension.lowercased()) && $0.lastPathComponent != "ClipBubble" }) { [weak self] url, date in
                self?.model.db.insertIfMissing(kind: "file", text: url.path, source: "Downloads", hash: "f:" + sha(url.path), created: date)
                self?.captured()
            }
        } else {
            downloads?.stop()
            downloads = nil
        }
    }

    /// Backfills the last 7 days of screenshots and downloads. Cheap and idempotent, so it also reruns after access is granted.
    private func importFolders() {
        let (shots, dls) = (screenshots, downloads)
        // Listing a big Downloads folder and reading every file's dates is slow: do it off the main thread.
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let found = (shots?.existing(days: 7) ?? []).map { ($0, $1, "Screenshot") }
                + (dls?.existing(days: 7) ?? []).map { ($0, $1, "Downloads") }
            DispatchQueue.main.async {
                guard let self else { return }
                for (url, date, source) in found {
                    self.model.db.insertIfMissing(kind: "file", text: url.path, source: source, hash: "f:" + sha(url.path), created: date)
                }
                self.model.reload()
                self.model.reloadRecent()
            }
        }
    }

    private func hosted<V: View>(_ view: V, size: NSSize, radius: CGFloat) -> NSView {
        let container = glass(frame: NSRect(origin: .zero, size: size), radius: radius)
        let host = ClickThroughHostingView(rootView: view)
        host.frame = container.bounds
        host.autoresizingMask = [.width, .height]
        container.addSubview(host)
        return container
    }

    /// No main menu otherwise, and ⌘C/⌘V/⌘A in the search field need one.
    private func installEditMenu() {
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let item = NSMenuItem()
        item.submenu = edit
        let main = NSMenu()
        main.addItem(item)
        NSApp.mainMenu = main
    }

    // MARK: Picking

    /// Puts the clip on the clipboard and, unless copying only, pastes it into the app underneath.
    private func pick(_ c: Clip, paste: Bool) {
        let written = Board.write(c)
        hidePanel()
        hidePeek()
        // Its image or files are gone: say so rather than paste whatever was copied before.
        guard written else { NSSound.beep(); return }
        guard paste, UserDefaults.standard.object(forKey: "autoPaste") as? Bool ?? true else { return }
        guard AXIsProcessTrusted() else {
            if !askedForAccessibility {
                askedForAccessibility = true
                AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
            }
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            let src = CGEventSource(stateID: .combinedSessionState)
            for down in [true, false] {
                let e = CGEvent(keyboardEventSource: src, virtualKey: CGKeyCode(kVK_ANSI_V), keyDown: down)
                e?.flags = .maskCommand
                e?.post(tap: .cghidEventTap)
            }
        }
    }

    private func handleKey(_ e: NSEvent) -> NSEvent? {
        guard panel.isKeyWindow else { return e }
        if model.showSettings {
            if Int(e.keyCode) == kVK_Escape { model.showSettings = false; return nil }
            return e
        }
        let cmd = e.modifierFlags.contains(.command), opt = e.modifierFlags.contains(.option)
        switch Int(e.keyCode) {
        case kVK_DownArrow: model.move(1)
        case kVK_UpArrow: model.move(-1)
        case kVK_Return, kVK_ANSI_KeypadEnter: if let c = model.selected { pick(c, paste: !opt) }
        case kVK_Tab: model.cycleFilter(e.modifierFlags.contains(.shift) ? -1 : 1)
        case kVK_Escape:
            if model.previewing { model.previewing = false }
            else if !model.query.isEmpty { model.query = "" }
            else { hidePanel() }
        case kVK_Delete where cmd: if let c = model.selected { model.delete(c) }
        case kVK_ANSI_Y where cmd: model.previewing.toggle()
        default:
            if cmd, let d = Int(e.charactersIgnoringModifiers ?? ""), (1...9).contains(d), d <= model.clips.count {
                pick(model.clips[d - 1], paste: !opt)
                return nil
            }
            return e
        }
        return nil
    }

    // MARK: Corner magnet

    /// Moves the bubble to the nearest screen corner (or the saved one when not dragging), with a little overshoot.
    private func snap(animated: Bool) {
        let center = NSPoint(x: bubble.frame.midX, y: bubble.frame.midY)
        let screen = (animated ? NSScreen.screens.first { $0.frame.contains(center) } : nil) ?? bubble.screen ?? NSScreen.main
        guard let area = screen?.visibleFrame else { return }
        var corner = UserDefaults.standard.string(forKey: "corner") ?? "br"
        if animated {
            corner = (center.y > area.midY ? "t" : "b") + (center.x > area.midX ? "r" : "l")
            UserDefaults.standard.set(corner, forKey: "corner")
        }
        let size = bubble.frame.size
        let target = NSRect(
            x: corner.hasSuffix("l") ? area.minX + Self.margin : area.maxX - size.width - Self.margin,
            y: corner.hasPrefix("b") ? area.minY + Self.margin : area.maxY - size.height - Self.margin,
            width: size.width, height: size.height)
        guard animated else { bubble.setFrame(target, display: true); return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.32
            ctx.timingFunction = CAMediaTimingFunction(controlPoints: 0.3, 1.35, 0.5, 1)
            bubble.animator().setFrame(target, display: true)
        }, completionHandler: { [weak self] in self?.restBubble() })
    }

    /// The mascot hops and the bubble flashes opaque whenever something is captured.
    private func captured() {
        model.reloadRecent()
        if panel.isVisible { model.reload() } else { model.unseen += 1 }
        if peek.isVisible { resizePeek() }
        model.pulse = true
        model.pulseTicks += 1
        bubble.animator().alphaValue = 1
        pulseWork?.cancel()
        let w = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.model.pulse = false
            self.restBubble()
        }
        pulseWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.1, execute: w)
    }

    private func restBubble() {
        if !panel.isVisible && !peek.isVisible && !model.bubbleHover && !model.pulse && abs(bubble.alphaValue - Self.idleAlpha) > 0.01 {
            bubble.animator().alphaValue = Self.idleAlpha
        }
    }

    // MARK: Panel

    func toggle() { panel.isVisible ? hidePanel() : showPanel() }

    /// Opens the panel anchored to the bubble's corner, growing toward the screen center.
    private func showPanel() {
        hidePeek()
        model.query = ""
        model.filter = "all"
        model.previewing = false
        model.showSettings = false
        model.reload()
        model.selection = model.clips.first?.id
        model.unseen = 0
        let b = bubble.frame
        let area = (bubble.screen ?? NSScreen.main)?.visibleFrame ?? .zero
        let size = panel.frame.size
        var x = b.midX > area.midX ? b.maxX - size.width - 9 : b.minX + 9
        var y = b.midY > area.midY ? b.minY - size.height + 4 : b.maxY - 4
        x = min(max(x, area.minX + 8), area.maxX - size.width - 8)
        y = min(max(y, area.minY + 8), area.maxY - size.height - 8)
        panel.setFrameOrigin(NSPoint(x: x, y: y))
        panel.alphaValue = 0
        panel.makeKeyAndOrderFront(nil)
        model.shown += 1
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.1
            panel.animator().alphaValue = 1
        }
        bubble.alphaValue = 1
        clickMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            self?.hidePanel()
        }
    }

    /// Instant, so a paste lands without waiting on an animation.
    private func hidePanel() {
        if let m = clickMonitor { NSEvent.removeMonitor(m); clickMonitor = nil }
        guard panel.isVisible else { return }
        panel.orderOut(nil)
        restBubble()
    }

    // MARK: Peek

    private func updatePeek() {
        peekWork?.cancel()
        let want = (model.bubbleHover || model.peekHover) && !panel.isVisible && !dragging
        if want && peek.isVisible { return }
        let w = DispatchWorkItem { [weak self] in want ? self?.showPeek() : self?.hidePeek() }
        peekWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + (want ? 0.3 : 0.2), execute: w)
        if want { bubble.animator().alphaValue = 1 } else { restBubble() }
    }

    private func resizePeek() {
        let size = peekHost.fittingSize
        let b = bubble.frame
        let area = (bubble.screen ?? NSScreen.main)?.visibleFrame ?? .zero
        var x = b.midX > area.midX ? b.minX - size.width - 4 : b.maxX + 4
        var y = b.midY > area.midY ? b.maxY - 9 - size.height : b.minY + 9
        x = min(max(x, area.minX + 8), area.maxX - size.width - 8)
        y = min(max(y, area.minY + 8), area.maxY - size.height - 8)
        peek.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
    }

    private func showPeek() {
        // Re-check at show time: a click may have opened the panel while this was scheduled.
        guard !peek.isVisible, !panel.isVisible, !dragging, model.bubbleHover || model.peekHover else { return }
        model.reloadRecent()
        model.shown += 1
        model.unseen = 0
        resizePeek()
        peek.alphaValue = 0
        peek.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.12
            peek.animator().alphaValue = 1
        }
    }

    private func hidePeek() {
        peekWork?.cancel()
        if model.peekHover { model.peekHover = false }   // a repeat would still redraw every view of the model
        guard peek.isVisible else { return }
        peek.orderOut(nil)
        restBubble()
    }

    /// ⌃⌥V toggles the panel from anywhere.
    private func registerHotKey() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, ctx in
            let me = Unmanaged<AppDelegate>.fromOpaque(ctx!).takeUnretainedValue()
            DispatchQueue.main.async { me.toggle() }
            return noErr
        }, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), nil)
        RegisterEventHotKey(UInt32(kVK_ANSI_V), UInt32(controlKey | optionKey),
                            EventHotKeyID(signature: OSType(0x434C4950), id: 1), GetApplicationEventTarget(), 0, &hotKey)
    }
}
