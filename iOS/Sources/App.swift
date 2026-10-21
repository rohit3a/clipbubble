import SwiftUI
import UniformTypeIdentifiers
import VisionKit

@main
struct ClipBubbleApp: App {
    @StateObject private var model = Model()

    var body: some Scene {
        WindowGroup {
            HistoryView().environmentObject(model)
                .onOpenURL { model.open($0) }
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
        if Clipboard.write(c) { say("Copied") }
    }

    /// Paste: what's on this iPhone's clipboard joins the history and goes to every linked computer.
    func paste(_ providers: [NSItemProvider]) {
        Task {
            let added = await Inbox.add(providers, source: "Clipboard")
            reload()
            say(added.isEmpty ? "Nothing to paste" : hosts.isEmpty ? "Saved" : statuses.values.contains { $0.ok } ? "Sending…" : "Saved, sends when linked")
        }
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
    @State private var showHosts = false

    var body: some View {
        NavigationStack {
            List {
                if model.hosts.isEmpty {
                    Section {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("Copy on your iPhone, paste on your computer, and back.").font(.headline)
                            Text("Link a Mac, Linux or Windows computer running ClipBubble.").font(.subheadline).foregroundStyle(.secondary)
                            Button("Link a computer") { showHosts = true }.buttonStyle(.borderedProminent).padding(.top, 4)
                        }
                        .padding(.vertical, 6)
                    }
                } else {
                    Section {
                        ForEach(model.hosts, id: \.self) { h in
                            let s = model.statuses[h]
                            Label {
                                Text(s?.text ?? "Connecting…")
                            } icon: {
                                Circle().fill(s?.ok == true ? Color.green : Color.orange).frame(width: 8, height: 8)
                            }
                            .font(.footnote).foregroundStyle(.secondary)
                        }
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
                }
            }
            .searchable(text: $model.query, prompt: "Search history")
            .refreshable { model.redialAll() }
            .navigationTitle("ClipBubble")
            .toolbar { Button { showHosts = true } label: { Image(systemName: "desktopcomputer") }.accessibilityLabel("Computers") }
            .sheet(isPresented: $showHosts) { HostsView().environmentObject(model) }
            .safeAreaInset(edge: .bottom) {
                VStack(spacing: 8) {
                    if let t = model.toast {
                        Text(t).font(.subheadline.weight(.medium)).padding(.horizontal, 14).padding(.vertical, 8)
                            .background(.thinMaterial, in: Capsule()).transition(.opacity)
                    }
                    // The system Paste button reads the clipboard without the "Allow Paste" prompt.
                    PasteButton(supportedContentTypes: [.image, .fileURL, .url, .plainText, .data]) { model.paste($0) }
                        .labelStyle(.titleAndIcon).buttonBorderShape(.capsule).controlSize(.large).tint(.accentColor)
                }
                .padding(.bottom, 8).animation(.default, value: model.toast)
            }
            .alert("Link a computer", isPresented: Binding(get: { model.pairing != nil && model.pairing != "Linking…" }, set: { if !$0 { model.pairing = nil } })) {
                Button("OK") { model.pairing = nil }
            } message: { Text(model.pairing ?? "") }
        }
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
        VStack(alignment: .leading, spacing: 4) {
            if let p = clip.imagePath, !clip.remote {
                Thumb(p, pixels: 720).scaledToFit().frame(maxHeight: 120).clipShape(RoundedRectangle(cornerRadius: 8))
            } else {
                Text(clip.kind == "file" ? clip.paths.map { ($0 as NSString).lastPathComponent }.joined(separator: ", ")
                     : clip.kind == "image" ? "Image" : clip.text)
                    .lineLimit(3).font(clip.kind == "text" ? .body : .callout)
            }
            HStack(spacing: 6) {
                Text([clip.source, device].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                Text(clip.created, style: .relative)
                if clip.remote { Image(systemName: "icloud.and.arrow.down") }
            }
            .font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct HostsView: View {
    @EnvironmentObject var model: Model
    @Environment(\.dismiss) private var dismiss
    @State private var alias = ""
    @State private var scanning = false

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

                Section {
                    Text("Settings → General → Keyboard → Keyboards → Add New Keyboard → ClipBubble, then turn on Allow Full Access so it can reach your computers.")
                    Button("Open Settings") { UIApplication.shared.open(URL(string: UIApplication.openSettingsURLString)!) }
                } header: { Text("Clipboard keyboard") } footer: {
                    Text("Your history in any app: tap an item to type it. Full Access is what lets it link to your computers; it never logs what you type.")
                }
            }
            .navigationTitle("Computers")
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
