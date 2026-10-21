import PhotosUI
import SwiftUI
import UniformTypeIdentifiers
import VisionKit

@main
struct ClipBubbleApp: App {
    @StateObject private var model = Model()
    @AppStorage("appearance") private var appearance = Appearance.system
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            HistoryView().environmentObject(model)
                .onOpenURL { model.open($0) }
                // Set on the window, not with preferredColorScheme, which leaves sheets behind and doesn't switch back to System.
                .onChange(of: appearance, initial: true) {
                    for case let scene as UIWindowScene in UIApplication.shared.connectedScenes {
                        scene.windows.forEach { $0.overrideUserInterfaceStyle = appearance.style }
                    }
                }
                // While the app is open it holds the link, and the keyboard leaves it alone (see SyncEngine.suspend).
                .onChange(of: phase, initial: true) {
                    guard phase != .inactive else { return }
                    Platform.defaults.set(phase == .active, forKey: "app.active")
                    SyncEngine.shared.suspend(phase == .background)
                }
        }
    }
}

enum Appearance: String, CaseIterable {
    case system = "System", light = "Light", dark = "Dark"

    var style: UIUserInterfaceStyle {
        switch self {
        case .system: return .unspecified
        case .light: return .light
        case .dark: return .dark
        }
    }
}

/// History and link state for the screens. Everything here is read and changed on the main thread.
@MainActor
final class Model: ObservableObject {
    @Published var clips: [Clip] = []
    @Published var hosts: [String] = SyncEngine.enabledHosts
    @Published var statuses: [String: LinkState] = [:]
    @Published var devices: [String: String] = [:]
    @Published var query = "" { didSet { reload() } }
    /// A short line at the bottom: what just happened.
    @Published var toast: String?
    @Published var pairing: String?
    private let db = DB()
    private var toastTask: Task<Void, Never>?

    init() {
        let engine = SyncEngine.shared
        engine.onReceived = { [weak self] in self?.reload() }
        engine.onStatus = { [weak self] in self?.statuses = engine.statuses; self?.reload() }
        engine.onApplied = { [weak self] _, peer in self?.say("On \(peer)'s clipboard") }
        engine.start()
        reload()
        // The share sheet and the keyboard write to the same history while the app is away.
        NotificationCenter.default.addObserver(forName: UIApplication.willEnterForegroundNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.hosts = SyncEngine.enabledHosts; self?.reload() }
        }
    }

    func reload() {
        clips = db.clips(query: query.isEmpty ? nil : query)
        devices = db.deviceNames()
    }

    func add(_ alias: String) {
        guard SSHTarget(alias) != nil, !hosts.contains(alias) else { return }
        hosts.append(alias)
        SyncEngine.shared.setHost(alias, on: true)
    }

    func remove(_ alias: String) {
        hosts.removeAll { $0 == alias }
        SyncEngine.shared.setHost(alias, on: false)
    }

    func redialAll() { hosts.forEach { SyncEngine.shared.redial($0) } }

    func delete(_ c: Clip) {
        db.delete(hash: c.hash)
        reload()
    }

    func copy(_ c: Clip) {
        guard Clipboard.write(c) else { return }
        UINotificationFeedbackGenerator().notificationOccurred(.success)
        say("Copied")
    }

    /// Paste: what's on this iPhone's clipboard joins the history and goes to every linked computer.
    func paste(_ providers: [NSItemProvider]) {
        Task {
            let added = await Inbox.add(providers, source: "Clipboard")
            reload()
            say(added.isEmpty ? "Nothing to paste" : hosts.isEmpty ? "Saved" : statuses.values.contains { $0.ok } ? "Sending…" : "Saved, sends when linked")
        }
    }

    func addText(_ t: String) {
        added(Inbox.text(t, source: "iPhone").map { [$0] } ?? [])
    }

    func addFiles(_ urls: [URL]) {
        added(urls.compactMap { Inbox.file($0, source: "iPhone") })
    }

    /// Pictures join as images, so they paste as pictures on the computer; videos join as files.
    func addPhotos(_ items: [PhotosPickerItem]) {
        say("Adding…")
        Task {
            var hashes: [String] = []
            for item in items {
                let h: String?
                if item.supportedContentTypes.contains(where: { $0.conforms(to: .image) }), let d = try? await item.loadTransferable(type: Data.self) {
                    h = await Task.detached { Inbox.image(d, source: "iPhone") }.value
                } else if let f = try? await item.loadTransferable(type: PickedFile.self) {
                    h = Inbox.file(f.url, source: "iPhone")
                } else { h = nil }
                if let h { hashes.append(h) }
            }
            added(hashes)
        }
    }

    /// Something added by hand is meant for the computers now, so files go over without waiting for Get.
    private func added(_ hashes: [String]) {
        reload()
        guard !hashes.isEmpty else { return say("Couldn't add that") }
        let engine = SyncEngine.shared
        for h in hashes where h.hasPrefix("f:") { engine.linked.forEach { engine.send(h, toDevice: $0.device) } }
        say(hosts.isEmpty ? "Added" : statuses.values.contains { $0.ok } ? "Sent to your computers" : "Added, sends when linked")
    }

    func open(_ url: URL) {
        guard url.host == "pair" else { return }
        pairing = "Linking…"
        Task {
            do {
                let (alias, name) = try await Pairing.pair(url)
                add(alias)
                SyncEngine.shared.redial(alias)
                pairing = nil
                say("Linked with \(name)")
            } catch {
                pairing = error.localizedDescription
            }
        }
    }

    func say(_ s: String) {
        toast = s
        toastTask?.cancel()
        toastTask = Task { try? await Task.sleep(nanoseconds: 2_500_000_000); if !Task.isCancelled { toast = nil } }
    }
}

struct HistoryView: View {
    @EnvironmentObject var model: Model
    @State private var showSettings = false
    @State private var adding: Adding?
    @State private var photos: [PhotosPickerItem] = []

    enum Adding: Identifiable { case text, photos, files; var id: Self { self } }

    var body: some View {
        NavigationStack {
            List {
                if model.hosts.isEmpty {
                    Section {
                        VStack(alignment: .leading, spacing: 10) {
                            Image(systemName: "laptopcomputer.and.iphone").font(.title).foregroundStyle(.tint)
                            Text("Copy on your iPhone, paste on your computer, and back.").font(.headline)
                            Text("Link a Mac, Linux or Windows computer running ClipBubble.").font(.subheadline).foregroundStyle(.secondary)
                            Button("Link a computer") { showSettings = true }.buttonStyle(.borderedProminent).padding(.top, 2)
                        }
                        .padding(.vertical, 8)
                    }
                } else {
                    Section {
                        ForEach(model.hosts, id: \.self) { h in HostRow(alias: h, state: model.statuses[h]) }
                    }
                }
                Section {
                    ForEach(model.clips, id: \.hash) { c in
                        ClipRow(clip: c, device: model.devices[c.origin])
                            .contentShape(Rectangle())
                            .onTapGesture { model.copy(c) }
                            .swipeActions { Button("Delete", role: .destructive) { model.delete(c) } }
                            .contextMenu { ClipMenu(clip: c) }
                    }
                } header: {
                    if !model.clips.isEmpty { Text("Recent") }
                }
            }
            .overlay {
                if model.clips.isEmpty {
                    ContentUnavailableView(model.query.isEmpty ? "Nothing copied yet" : "No matches",
                                           systemImage: model.query.isEmpty ? "doc.on.clipboard" : "magnifyingglass",
                                           description: Text(model.query.isEmpty ? "Copy something on a linked computer, or paste it here." : "Try other words."))
                        .padding(.top, model.hosts.isEmpty ? 220 : 120)
                }
            }
            .searchable(text: $model.query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Search history")
            .refreshable { model.redialAll() }
            .navigationTitle("ClipBubble")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { showSettings = true } label: { Image(systemName: "gearshape") }.accessibilityLabel("Settings")
                }
            }
            .sheet(isPresented: $showSettings) { HostsView().environmentObject(model) }
            .safeAreaInset(edge: .bottom) {
                VStack(spacing: 10) {
                    if let t = model.toast {
                        Label(t, systemImage: "checkmark.circle.fill").font(.subheadline.weight(.medium))
                            .padding(.horizontal, 16).padding(.vertical, 10)
                            .background(.regularMaterial, in: Capsule())
                            .shadow(color: .black.opacity(0.12), radius: 12, y: 4)
                            .transition(.move(edge: .bottom).combined(with: .opacity))
                    }
                    HStack(spacing: 10) {
                        // The system Paste button reads the clipboard without the "Allow Paste" prompt.
                        PasteButton(supportedContentTypes: [.image, .fileURL, .url, .plainText, .data]) { model.paste($0) }
                            .labelStyle(.titleAndIcon).buttonBorderShape(.capsule).controlSize(.large)
                        Menu {
                            Button { adding = .text } label: { Label("Text", systemImage: "text.cursor") }
                            Button { adding = .photos } label: { Label("Photos & Videos", systemImage: "photo.on.rectangle") }
                            Button { adding = .files } label: { Label("Files", systemImage: "folder") }
                        } label: {
                            Label("Add", systemImage: "plus").font(.body.weight(.semibold))
                                .padding(.horizontal, 18).frame(height: 50)
                                .background(.regularMaterial, in: Capsule())
                        }
                    }
                    .shadow(color: .black.opacity(0.15), radius: 10, y: 4)
                }
                .padding(.bottom, 8)
            }
            .animation(.spring(duration: 0.3), value: model.toast)
            .sheet(isPresented: Binding(get: { adding == .text }, set: { if !$0 { adding = nil } })) {
                TextComposer { model.addText($0) }
            }
            .photosPicker(isPresented: Binding(get: { adding == .photos }, set: { if !$0 { adding = nil } }),
                          selection: $photos, maxSelectionCount: 20, matching: .any(of: [.images, .videos]))
            .onChange(of: photos) {
                guard !photos.isEmpty else { return }
                model.addPhotos(photos)
                photos = []
            }
            .fileImporter(isPresented: Binding(get: { adding == .files }, set: { if !$0 { adding = nil } }),
                          allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
                if case .success(let urls) = result { model.addFiles(urls) }
            }
            .alert("Link a computer", isPresented: Binding(get: { model.pairing != nil && model.pairing != "Linking…" }, set: { if !$0 { model.pairing = nil } })) {
                Button("OK") { model.pairing = nil }
            } message: { Text(model.pairing ?? "") }
        }
    }
}

/// Type or paste a note to add to the history.
struct TextComposer: View {
    let add: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        NavigationStack {
            TextEditor(text: $text)
                .focused($focused)
                .padding(.horizontal, 12)
                .navigationTitle("New text")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Add") { add(text); dismiss() }
                            .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                }
                .onAppear { focused = true }
        }
        .presentationDetents([.medium, .large])
    }
}

/// A video or other file from the photo library, copied somewhere that outlives the picker.
struct PickedFile: Transferable {
    let url: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .item) { received in
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let dest = dir.appendingPathComponent(received.file.lastPathComponent)
            try FileManager.default.copyItem(at: received.file, to: dest)
            return PickedFile(url: dest)
        }
    }
}

struct HostRow: View {
    let alias: String
    let state: LinkState?

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "laptopcomputer").font(.title3).foregroundStyle(.secondary).frame(width: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text(name).font(.subheadline.weight(.semibold))
                Text(state.map { if case .live = $0 { return "Linked" }; return $0.text } ?? "Connecting…")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Circle().fill(state?.ok == true ? Color.green : Color.orange).frame(width: 8, height: 8)
        }
        .padding(.vertical, 2)
    }

    private var name: String {
        if case .live(let h) = state { return h.name }
        return alias.split(separator: "@").last.map(String.init) ?? alias
    }
}

struct ClipMenu: View {
    @EnvironmentObject var model: Model
    let clip: Clip

    var body: some View {
        Button { model.copy(clip) } label: { Label("Copy", systemImage: "doc.on.doc") }
        if clip.remote {
            Button { SyncEngine.shared.get(clip.hash) } label: { Label("Get from \(model.devices[clip.origin] ?? "computer")", systemImage: "icloud.and.arrow.down") }
        } else if clip.kind == "text" {
            ShareLink(item: clip.text) { Label("Share", systemImage: "square.and.arrow.up") }
        } else if let p = clip.file ?? clip.paths.first {
            ShareLink(item: URL(fileURLWithPath: p)) { Label("Share or Save", systemImage: "square.and.arrow.up") }
        }
        Button(role: .destructive) { model.delete(clip) } label: { Label("Delete", systemImage: "trash") }
    }
}

struct ClipRow: View {
    let clip: Clip
    let device: String?

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: icon)
                .font(.system(size: 14, weight: .semibold)).foregroundStyle(color)
                .frame(width: 30, height: 30).background(color.opacity(0.15), in: RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 6) {
                if let p = clip.imagePath, !clip.remote {
                    // The frame sets the size and the image fills it, so a wide screenshot can't push the row out.
                    Color.clear.frame(height: 150).frame(maxWidth: .infinity)
                        .overlay { Thumb(p, pixels: 900).scaledToFill() }
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                } else {
                    Text(clip.kind == "file" ? clip.paths.map { ($0 as NSString).lastPathComponent }.joined(separator: ", ")
                         : clip.kind == "image" ? "Image" : clip.text.trimmingCharacters(in: .whitespacesAndNewlines))
                        .font(.body).lineLimit(3)
                }
                Text([device ?? clip.source, clip.created.formatted(.relative(presentation: .named, unitsStyle: .abbreviated))]
                        .filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 4)
    }

    private var icon: String {
        if clip.remote { return "icloud.and.arrow.down" }
        switch clip.kind {
        case "text": return clip.text.hasPrefix("http") ? "link" : "text.alignleft"
        case "image": return "photo"
        default: return clip.imagePath != nil ? "photo" : "doc"
        }
    }

    private var color: Color {
        switch icon {
        case "link": return .blue
        case "photo": return .purple
        case "doc": return .orange
        case "icloud.and.arrow.down": return .teal
        default: return .gray
        }
    }
}

struct HostsView: View {
    @EnvironmentObject var model: Model
    @Environment(\.dismiss) private var dismiss
    @State private var alias = ""
    @State private var scanning = false
    @AppStorage("appearance") private var appearance = Appearance.system

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text("On your Mac, open ClipBubble → Settings → Sync over SSH → Link iPhone…, then scan the code with this iPhone's Camera.")
                    if DataScannerViewController.isSupported {
                        Button { scanning = true } label: { Label("Scan code", systemImage: "qrcode.viewfinder") }
                    }
                } header: { Text("Link a Mac") }

                if !model.hosts.isEmpty {
                    Section("Computers") {
                        ForEach(model.hosts, id: \.self) { h in
                            VStack(alignment: .leading) {
                                Text(h)
                                Text(model.statuses[h]?.text ?? "Connecting…").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .onDelete { $0.map { model.hosts[$0] }.forEach(model.remove) }
                    }
                }

                Section {
                    HStack {
                        TextField("you@computer.local", text: $alias)
                            .textInputAutocapitalization(.never).autocorrectionDisabled().keyboardType(.URL)
                        Button("Add") { model.add(alias.trimmingCharacters(in: .whitespaces)); alias = "" }
                            .disabled(SSHTarget(alias.trimmingCharacters(in: .whitespaces)) == nil)
                    }
                    Text(Keys.publicLine).font(.caption.monospaced()).textSelection(.enabled)
                    Button("Copy this iPhone's key") { UIPasteboard.general.string = Keys.publicLine }
                } header: { Text("Linux, Windows, or by hand") } footer: {
                    Text("Add the key as a line in ~/.ssh/authorized_keys on the computer, with SSH turned on there.")
                }

                Section("Appearance") {
                    Picker("Appearance", selection: $appearance) {
                        ForEach(Appearance.allCases, id: \.self) { Text($0.rawValue) }
                    }
                    .pickerStyle(.segmented)
                }

                Section {
                    Text("Settings → General → Keyboard → Keyboards → Add New Keyboard → ClipBubble, then turn on Allow Full Access so it can reach your computers.")
                    Button("Open Settings") { UIApplication.shared.open(URL(string: UIApplication.openSettingsURLString)!) }
                } header: { Text("Clipboard keyboard") } footer: {
                    Text("Your history in any app: tap an item to type it. Full Access is what lets it link to your computers; it never logs what you type.")
                }
            }
            .navigationTitle("Settings")
            .toolbar { Button("Done") { dismiss() } }
            .sheet(isPresented: $scanning) {
                Scanner { url in scanning = false; dismiss(); model.open(url) }.ignoresSafeArea()
            }
        }
    }
}

/// The camera, looking for the Mac's pairing code.
struct Scanner: UIViewControllerRepresentable {
    let found: (URL) -> Void

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let v = DataScannerViewController(recognizedDataTypes: [.barcode(symbologies: [.qr])], isHighlightingEnabled: true)
        v.delegate = context.coordinator
        try? v.startScanning()
        return v
    }

    func updateUIViewController(_ v: DataScannerViewController, context: Context) {}
    func makeCoordinator() -> Coordinator { Coordinator(found: found) }

    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        let found: (URL) -> Void
        private var done = false
        init(found: @escaping (URL) -> Void) { self.found = found }

        func dataScanner(_ s: DataScannerViewController, didAdd items: [RecognizedItem], allItems: [RecognizedItem]) {
            for case .barcode(let b) in items {
                guard !done, let v = b.payloadStringValue, let url = URL(string: v), url.scheme == "clipbubble" else { continue }
                done = true
                found(url)
            }
        }
    }
}
