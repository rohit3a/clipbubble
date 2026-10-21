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

    /// Everything kept, for the Library.
    func library(query: String) -> [Clip] {
        db.clips(limit: 5000, query: query.isEmpty ? nil : query)
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

/// The app: Recent, Library and Settings as tabs in the system tab bar, with Add as a tab that opens a panel.
struct HistoryView: View {
    @EnvironmentObject var model: Model
    @State private var tab = Tab.recent
    @State private var adding: Adding?
    /// What the Add panel asked for, opened once the panel has gone.
    @State private var next: Adding?
    @State private var photos: [PhotosPickerItem] = []
    @State private var viewing: Clip?

    enum Tab { case recent, library, add, settings }
    enum Adding { case panel, text, photos, files }

    var body: some View {
        // Add isn't a place to go, so choosing it opens the panel and leaves the current tab selected.
        TabView(selection: Binding(get: { tab }, set: { if $0 == .add { adding = .panel } else { tab = $0 } })) {
            NavigationStack { recent }
                .tabItem { Label("Recent", systemImage: "clock") }.tag(Tab.recent)
            NavigationStack { LibraryView() }
                .tabItem { Label("Library", systemImage: "square.grid.2x2") }.tag(Tab.library)
            Color.clear
                .tabItem { Label("Add", systemImage: "plus.circle") }.tag(Tab.add)
            SettingsView()
                .tabItem { Label("Settings", systemImage: "gearshape") }.tag(Tab.settings)
        }
        .minimizesOnScroll()
        .sensoryFeedback(.selection, trigger: tab)
        .sheet(isPresented: shows(.panel), onDismiss: { adding = next; next = nil }) {
            AddPanel(paste: model.paste) { next = $0; adding = nil }
        }
        .sheet(isPresented: shows(.text)) { TextComposer { model.addText($0) } }
        .photosPicker(isPresented: shows(.photos), selection: $photos, maxSelectionCount: 20, matching: .any(of: [.images, .videos]))
        .onChange(of: photos) {
            guard !photos.isEmpty else { return }
            model.addPhotos(photos)
            photos = []
        }
        .fileImporter(isPresented: shows(.files), allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            if case .success(let urls) = result { model.addFiles(urls) }
        }
        .alert("Link a computer", isPresented: Binding(get: { model.pairing != nil && model.pairing != "Linking…" }, set: { if !$0 { model.pairing = nil } })) {
            Button("OK") { model.pairing = nil }
        } message: { Text(model.pairing ?? "") }
    }

    private func shows(_ a: Adding) -> Binding<Bool> {
        Binding(get: { adding == a }, set: { if !$0, adding == a { adding = nil } })
    }

    private var recent: some View {
        List {
            if model.hosts.isEmpty {
                Section {
                    VStack(alignment: .leading, spacing: 10) {
                        Image(systemName: "laptopcomputer.and.iphone").font(.title).foregroundStyle(.tint)
                        Text("Copy on your iPhone, paste on your computer, and back.").font(.headline)
                        Text("Link a Mac, Linux or Windows computer running ClipBubble.").font(.subheadline).foregroundStyle(.secondary)
                        Button("Link a computer") { tab = .settings }.buttonStyle(.borderedProminent).padding(.top, 2)
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
                    Button { model.copy(c) } label: { ClipRow(clip: c, device: model.devices[c.origin]) }
                        .tint(.primary)
                        .swipeActions { Button("Delete", role: .destructive) { model.delete(c) } }
                        .clipMenu(c) { viewing = c }
                }
            } header: {
                if !model.clips.isEmpty { Text("Recent") }
            }
        }
        .animation(.snappy, value: model.clips.map(\.hash))
        .overlay {
            if model.clips.isEmpty {
                ContentUnavailableView(model.query.isEmpty ? "Nothing copied yet" : "No matches",
                                       systemImage: model.query.isEmpty ? "doc.on.clipboard" : "magnifyingglass",
                                       description: Text(model.query.isEmpty ? "Copy something on a linked computer, or tap Add." : "Try other words."))
                    .padding(.top, model.hosts.isEmpty ? 220 : 120)
            }
        }
        .searchable(text: $model.query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Search history")
        .refreshable { model.redialAll() }
        .navigationTitle("ClipBubble")
        .navigationDestination(item: $viewing) { ClipDetail(clip: $0) }
        .toast(model.toast)
    }
}

/// Add: paste what's on this iPhone's clipboard, type a note, or pick photos or files.
struct AddPanel: View {
    let paste: ([NSItemProvider]) -> Void
    let pick: (HistoryView.Adding) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 18) {
            Text("Add to ClipBubble").font(.headline)
            // The system Paste button reads the clipboard without the "Allow Paste" prompt.
            PasteButton(supportedContentTypes: [.image, .fileURL, .url, .plainText, .data]) { p in
                paste(p)
                Task { @MainActor in dismiss() }
            }
            .labelStyle(.titleAndIcon).buttonBorderShape(.capsule).controlSize(.extraLarge)
            HStack(spacing: 12) {
                option("Text", "text.cursor", .text)
                option("Photos", "photo.on.rectangle", .photos)
                option("Files", "folder", .files)
            }
        }
        .padding(.horizontal, 20)
        .padding(.top, 24)
        .frame(maxHeight: .infinity, alignment: .top)
        .presentationDetents([.height(270)])
        .presentationCornerRadius(32)
    }

    private func option(_ title: String, _ symbol: String, _ a: HistoryView.Adding) -> some View {
        Button { pick(a) } label: {
            VStack(spacing: 8) {
                Image(systemName: symbol).font(.title2)
                Text(title).font(.subheadline.weight(.medium))
            }
            .frame(maxWidth: .infinity, minHeight: 92)
            .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        }
        .buttonStyle(Pressable())
    }
}

/// Items dip when touched.
struct Pressable: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.96 : 1)
            .opacity(configuration.isPressed ? 0.85 : 1)
            .animation(.snappy(duration: 0.18), value: configuration.isPressed)
    }
}

extension View {
    /// The short "Copied" line, just above the tab bar.
    func toast(_ text: String?) -> some View {
        safeAreaInset(edge: .bottom) {
            if let text {
                Label(text, systemImage: "checkmark.circle.fill").font(.subheadline.weight(.medium))
                    .padding(.horizontal, 16).padding(.vertical, 10)
                    .background(.regularMaterial, in: Capsule())
                    .shadow(color: .black.opacity(0.12), radius: 12, y: 4)
                    .padding(.bottom, 8)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.spring(duration: 0.35), value: text)
    }

    /// iOS 26 shrinks the tab bar while you scroll down a list.
    @ViewBuilder func minimizesOnScroll() -> some View {
        if #available(iOS 26, *) { tabBarMinimizeBehavior(.onScrollDown) } else { self }
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

    private var icon: String { clip.remote ? "icloud.and.arrow.down" : clip.look.symbol }
    private var color: Color { clip.remote ? .teal : clip.look.tint }
}

struct SettingsView: View {
    @EnvironmentObject var model: Model
    @State private var alias = ""
    @State private var scanning = false
    @AppStorage("appearance") private var appearance = Appearance.system

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text("On your Mac, Windows PC or Linux computer, open ClipBubble → Settings → Sync over SSH → Link iPhone…, then scan the code with this iPhone's Camera.")
                    if DataScannerViewController.isSupported {
                        Button { scanning = true } label: { Label("Scan code", systemImage: "qrcode.viewfinder") }
                    }
                } header: { Text("Link a computer") }

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
            .sheet(isPresented: $scanning) {
                Scanner { url in scanning = false; model.open(url) }.ignoresSafeArea()
            }
        }
    }
}

/// The camera, looking for a computer's pairing code.
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
