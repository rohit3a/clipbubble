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
}

@MainActor
final class KeyboardModel: ObservableObject {
    @Published var clips: [Clip] = []
    @Published var status = ""
    @Published var linked = false
    @Published var fullAccess = true
    @Published var globe = false
    @Published var note: String?
    private weak var keyboard: UIInputViewController?
    private var started = false

    init(keyboard: UIInputViewController) { self.keyboard = keyboard }

    /// Without Full Access a keyboard can't see the App Group or the network, so there's nothing to show yet.
    func appear(fullAccess: Bool, globe: Bool) {
        self.fullAccess = fullAccess
        self.globe = globe
        guard fullAccess else { return }
        reload()
        let engine = SyncEngine.shared
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
        clips = DB().clips(limit: 40)
        let live = SyncEngine.shared.statuses.values.compactMap { s -> String? in if case .live(let h) = s { return h.name }; return nil }
        linked = !live.isEmpty
        status = SyncEngine.enabledHosts.isEmpty ? "No computers linked yet"
            : linked ? live.joined(separator: ", ") : SyncEngine.shared.statuses.values.first?.text ?? "Connecting…"
    }

    func tap(_ c: Clip) {
        if c.kind == "text" {
            keyboard?.textDocumentProxy.insertText(c.text)
        } else if c.remote {
            SyncEngine.shared.get(c.hash)
            flash("Getting it from your computer…")
        } else if Clipboard.write(c) {
            flash(c.kind == "image" ? "Image copied. Touch and hold to paste." : "Copied. Touch and hold to paste.")
        }
    }

    func type(_ s: String) { keyboard?.textDocumentProxy.insertText(s) }
    func backspace() { keyboard?.textDocumentProxy.deleteBackward() }
    func nextKeyboard() { keyboard?.advanceToNextInputMode() }

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
                Text(model.note ?? model.status).font(.caption.weight(.medium)).foregroundStyle(.secondary).lineLimit(1)
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
                ScrollView {
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 6), GridItem(.flexible(), spacing: 6)], spacing: 6) {
                        ForEach(model.clips, id: \.hash) { c in
                            Button { model.tap(c) } label: { KeyboardChip(clip: c) }.buttonStyle(.plain)
                        }
                    }
                    .padding(.horizontal, 6)
                }
            }

            HStack(spacing: 6) {
                if model.globe {
                    key(Image(systemName: "globe"), width: 44) { model.nextKeyboard() }
                }
                key(Text("space"), width: nil) { model.type(" ") }
                key(Image(systemName: "delete.left"), width: 52) { model.backspace() }
                    .simultaneousGesture(LongPressGesture(minimumDuration: 0.4).onEnded { _ in
                        deleting = Timer.scheduledTimer(withTimeInterval: 0.08, repeats: true) { _ in Task { @MainActor in model.backspace() } }
                    })
                    .simultaneousGesture(DragGesture(minimumDistance: 0).onEnded { _ in deleting?.invalidate(); deleting = nil })
                key(Image(systemName: "return"), width: 60) { model.type("\n") }
            }
            .padding(.horizontal, 6).padding(.bottom, 6)
        }
    }

    private func key<L: View>(_ label: L, width: CGFloat?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            label.font(.body).frame(maxWidth: width ?? .infinity).frame(width: width, height: 42)
                .background(Color(.systemBackground).opacity(0.9), in: RoundedRectangle(cornerRadius: 8))
                .shadow(color: .black.opacity(0.25), radius: 0, y: 1)
        }
        .buttonStyle(.plain)
    }
}

struct KeyboardChip: View {
    let clip: Clip

    var body: some View {
        Group {
            if let p = clip.imagePath, !clip.remote, let img = UIImage(contentsOfFile: p)?.preparingThumbnail(of: CGSize(width: 240, height: 120)) {
                Image(uiImage: img).resizable().scaledToFill().frame(height: 58).clipped()
            } else {
                HStack(alignment: .top, spacing: 4) {
                    if clip.kind != "text" { Image(systemName: clip.remote ? "icloud.and.arrow.down" : "doc") }
                    Text(clip.kind == "text" ? clip.text.trimmingCharacters(in: .whitespacesAndNewlines)
                         : clip.kind == "image" ? "Image" : clip.paths.map { ($0 as NSString).lastPathComponent }.joined(separator: ", "))
                        .lineLimit(2).multilineTextAlignment(.leading)
                    Spacer(minLength: 0)
                }
                .font(.footnote).padding(8).frame(height: 58, alignment: .topLeading)
            }
        }
        .frame(maxWidth: .infinity)
        .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 8))
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .contentShape(Rectangle())
    }
}
