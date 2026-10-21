import SwiftUI
import UIKit

/// The clipboard keyboard: your history in any app, fresh from your computers. Tap text to type it; pictures and
/// files go on the clipboard to paste, since a keyboard can only type text.
final class KeyboardViewController: UIInputViewController {
    private lazy var model = KeyboardModel(keyboard: self)

    override func viewDidLoad() {
        super.viewDidLoad()
        let host = UIHostingController(rootView: KeyboardView(model: model))
        host.view.backgroundColor = .clear
        host.view.translatesAutoresizingMaskIntoConstraints = false
        addChild(host)
        view.addSubview(host.view)
        NSLayoutConstraint.activate([
            host.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            host.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            host.view.topAnchor.constraint(equalTo: view.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            host.view.heightAnchor.constraint(equalToConstant: 286),
        ])
        host.didMove(toParent: self)
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        model.appear(fullAccess: hasFullAccess, globe: needsInputModeSwitchKey)
    }

    /// A dismissed keyboard can stay alive in the background; it lets go of the link so the app can have it.
    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        if hasFullAccess { SyncEngine.shared.suspend(true) }
    }
}

@MainActor
final class KeyboardModel: ObservableObject {
    @Published var clips: [Clip] = []
    @Published var status = ""
    @Published var linked = false
    @Published var fullAccess = true
    @Published var globe = false
    @Published var note: String?
    /// Counts each time the keyboard comes up.
    @Published var opened = 0
    private var viaApp = false
    private weak var keyboard: UIInputViewController?
    private var started = false
    private lazy var db = DB()
    /// A picture fetched from a computer by a tap goes on the clipboard as soon as it arrives.
    private var fetching: String?

    init(keyboard: UIInputViewController) { self.keyboard = keyboard }

    /// Without Full Access a keyboard can't see the App Group or the network, so there's nothing to show yet.
    func appear(fullAccess: Bool, globe: Bool) {
        self.fullAccess = fullAccess
        self.globe = globe
        guard fullAccess else { return }
        opened += 1
        // Inside the ClipBubble app, the app already holds the link and this keyboard reads what it syncs.
        viaApp = Platform.defaults.bool(forKey: "app.active")
        let engine = SyncEngine.shared
        engine.suspend(viaApp)
        reload()
        if !started {
            started = true
            engine.onReceived = { [weak self] in self?.reload() }
            engine.onStatus = { [weak self] in self?.reload() }
            engine.start()
        } else {
            SyncEngine.enabledHosts.forEach(engine.redial)
        }
    }

    func reload() {
        clips = db.clips(limit: 40)
        if let h = fetching, let c = clips.first(where: { $0.hash == h && !$0.remote }) {
            fetching = nil
            copy(c)
        }
        let live = SyncEngine.shared.statuses.values.compactMap { s -> String? in if case .live(let h) = s { return h.name }; return nil }
        linked = viaApp || !live.isEmpty
        status = SyncEngine.enabledHosts.isEmpty ? "No computers linked yet"
            : viaApp ? "Synced by the ClipBubble app"
            : !live.isEmpty ? live.joined(separator: ", ") : SyncEngine.shared.statuses.values.first?.text ?? "Connecting…"
    }

    func tap(_ c: Clip) {
        tick()
        if c.kind == "text" {
            keyboard?.textDocumentProxy.insertText(c.text)
        } else if c.remote {
            fetching = c.hash
            SyncEngine.shared.get(c.hash)
            flash("Getting it from your computer…")
        } else {
            copy(c)
        }
    }

    /// iOS lets a keyboard type text but not insert pictures or files, so those go on the clipboard to paste.
    private func copy(_ c: Clip) {
        guard Clipboard.write(c) else { return }
        flash(c.imagePath != nil ? "Image copied. Touch and hold the text box, then Paste." : "Copied. Touch and hold the text box, then Paste.")
    }

    func type(_ s: String) { tick(); keyboard?.textDocumentProxy.insertText(s) }
    func backspace() { tick(); keyboard?.textDocumentProxy.deleteBackward() }
    func nextKeyboard() { keyboard?.advanceToNextInputMode() }

    private let haptic = UIImpactFeedbackGenerator(style: .light)
    private func tick() { haptic.impactOccurred(intensity: 0.6) }

    private func flash(_ s: String) {
        note = s
        Task { try? await Task.sleep(nanoseconds: 2_500_000_000); if note == s { note = nil } }
    }
}

struct KeyboardView: View {
    @ObservedObject var model: KeyboardModel
    @State private var deleting: Timer?

    var body: some View {
        VStack(spacing: 6) {
            HStack(spacing: 6) {
                Circle().fill(model.linked ? Color.green : Color.orange).frame(width: 7, height: 7)
                Text(model.note ?? model.status).font(.footnote.weight(.semibold)).foregroundStyle(.secondary).lineLimit(1)
                Spacer()
            }
            .padding(.horizontal, 10).padding(.top, 6)

            if !model.fullAccess {
                VStack(spacing: 6) {
                    Text("Turn on Allow Full Access").font(.headline)
                    Text("Settings → General → Keyboard → Keyboards → ClipBubble. It lets the keyboard read your ClipBubble history and link to your computers.")
                        .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                }
                .padding(.horizontal, 20).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if model.clips.isEmpty {
                Text("Nothing copied yet. Copy something on your computer and it shows up here.")
                    .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    .padding(.horizontal, 20).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { scroll in
                    ScrollView {
                        LazyVGrid(columns: [GridItem(.flexible(), spacing: 6), GridItem(.flexible(), spacing: 6)], spacing: 6) {
                            ForEach(model.clips, id: \.hash) { c in
                                Button { model.tap(c) } label: { KeyboardChip(clip: c) }.buttonStyle(Pressable())
                            }
                        }
                        .padding(.horizontal, 6)
                    }
                    .crispEdges()
                    // The newest copy is what you're most likely after, so every time the keyboard opens it's in view.
                    .onChange(of: model.opened) { scroll.scrollTo(model.clips.first?.hash, anchor: .top) }
                    .onChange(of: model.clips.first?.hash) { withAnimation { scroll.scrollTo(model.clips.first?.hash, anchor: .top) } }
                }
            }

            HStack(spacing: 6) {
                if model.globe {
                    key(Image(systemName: "globe"), width: 44, function: true) { model.nextKeyboard() }
                }
                key(Text("space"), width: nil) { model.type(" ") }
                key(Image(systemName: "delete.left"), width: 52, function: true) { model.backspace() }
                    .simultaneousGesture(LongPressGesture(minimumDuration: 0.4).onEnded { _ in
                        deleting = Timer.scheduledTimer(withTimeInterval: 0.08, repeats: true) { _ in Task { @MainActor in model.backspace() } }
                    })
                    .simultaneousGesture(DragGesture(minimumDistance: 0).onEnded { _ in deleting?.invalidate(); deleting = nil })
                key(Image(systemName: "return"), width: 60, function: true) { model.type("\n") }
            }
            .padding(.horizontal, 6).padding(.bottom, 6)
        }
    }

    private func key<L: View>(_ label: L, width: CGFloat?, function: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            label.font(.system(size: 18)).foregroundStyle(Color(.label))
                .frame(maxWidth: width ?? .infinity).frame(width: width, height: 42)
                .background(function ? KeyColor.function : KeyColor.letter, in: RoundedRectangle(cornerRadius: 8))
                .shadow(color: .black.opacity(0.3), radius: 0, y: 1)
        }
        .buttonStyle(Pressable())
    }
}

/// The system keyboard's key colors, so ClipBubble sits right next to it in light and dark.
private enum KeyColor {
    static let letter = Color(UIColor { $0.userInterfaceStyle == .dark ? UIColor(white: 0.42, alpha: 1) : .white })
    static let function = Color(UIColor { $0.userInterfaceStyle == .dark ? UIColor(white: 0.27, alpha: 1) : UIColor(red: 0.67, green: 0.70, blue: 0.74, alpha: 1) })
    static let chip = Color(UIColor { $0.userInterfaceStyle == .dark ? UIColor(white: 0.24, alpha: 1) : .white })
}

/// Keys and items dip when touched, like the system keyboard.
private struct Pressable: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.96 : 1)
            .brightness(configuration.isPressed ? -0.08 : 0)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

private extension View {
    /// iOS 26 blurs and fades content scrolling under the edges, which smears the top row of items.
    @ViewBuilder func crispEdges() -> some View {
        if #available(iOS 26, *) { scrollEdgeEffectHidden(true, for: .all) } else { self }
    }
}

struct KeyboardChip: View {
    let clip: Clip

    var body: some View {
        Group {
            if let p = clip.imagePath, !clip.remote {
                Color.clear.frame(height: 58).overlay { Thumb(p, pixels: 540).scaledToFill() }.clipped()
            } else {
                HStack(alignment: .top, spacing: 4) {
                    if clip.kind != "text" { Image(systemName: clip.remote ? "icloud.and.arrow.down" : "doc") }
                    Text(clip.kind == "text" ? clip.text.trimmingCharacters(in: .whitespacesAndNewlines)
                         : clip.kind == "image" ? "Image" : clip.paths.map { ($0 as NSString).lastPathComponent }.joined(separator: ", "))
                        .lineLimit(2).multilineTextAlignment(.leading)
                    Spacer(minLength: 0)
                }
                .font(.footnote).foregroundStyle(Color(.label)).padding(8).frame(height: 58, alignment: .topLeading)
            }
        }
        .frame(maxWidth: .infinity)
        .background(KeyColor.chip)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .shadow(color: .black.opacity(0.25), radius: 0, y: 1)
        .contentShape(Rectangle())
    }
}
