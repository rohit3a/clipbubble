import SwiftUI

extension View {
    /// Long-press: a large preview of the whole item, with Open first in the menu. A tap stays Copy.
    func clipMenu(_ clip: Clip, open: @escaping () -> Void) -> some View {
        contextMenu { ClipMenu(clip: clip, open: open) } preview: { ClipPreview(clip: clip) }
    }
}

struct ClipMenu: View {
    @EnvironmentObject var model: Model
    let clip: Clip
    let open: () -> Void

    var body: some View {
        Button(action: open) { Label("Open", systemImage: "arrow.up.left.and.arrow.down.right") }
        Button { model.copy(clip) } label: { Label("Copy", systemImage: "doc.on.doc") }
        if clip.remote {
            Button { model.get(clip) } label: { Label("Get from \(model.devices[clip.origin] ?? "computer")", systemImage: "icloud.and.arrow.down") }
        } else {
            ClipShare(clip: clip)
        }
        Button(role: .destructive) { model.delete(clip) } label: { Label("Delete", systemImage: "trash") }
    }
}

struct ClipShare: View {
    @EnvironmentObject var model: Model
    let clip: Clip

    var body: some View {
        if clip.kind == "text" {
            ShareLink(item: model.whole(clip).text) { Label("Share", systemImage: "square.and.arrow.up") }
        } else if let p = clip.file ?? clip.paths.first {
            ShareLink(item: URL(fileURLWithPath: p)) { Label("Share or Save", systemImage: "square.and.arrow.up") }
        }
    }
}

/// What the long-press shows: the item at a readable size.
struct ClipPreview: View {
    let clip: Clip

    var body: some View {
        if let p = clip.imagePath, !clip.remote {
            Color.clear.frame(width: 340, height: 260).overlay { Thumb(p, pixels: 900).scaledToFit() }
        } else if clip.kind == "text" {
            Text(clip.text.trimmingCharacters(in: .whitespacesAndNewlines))
                .font(.body).lineLimit(18)
                .padding(18).frame(width: 340, alignment: .leading)
        } else {
            VStack(spacing: 10) {
                Image(systemName: clip.remote ? "icloud.and.arrow.down" : "doc.fill").font(.system(size: 44)).foregroundStyle(clip.remote ? .teal : .orange)
                Text(clip.names).font(.subheadline).multilineTextAlignment(.center).lineLimit(6)
            }
            .padding(24).frame(width: 300)
        }
    }
}

/// Everything about one item: all of its text, the full picture, or its files, and where and when it came from.
struct ClipDetail: View {
    @EnvironmentObject var model: Model
    @Environment(\.dismiss) private var dismiss
    /// Text this long already takes a while to scroll, and a megabyte of it freezes the app.
    private static let shownLength = 20_000
    private let item: Clip
    @State private var confirmDelete = false

    init(clip: Clip) { item = clip }

    @State private var live: Clip?
    private var clip: Clip { live ?? item }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                if let t = model.transfers[clip.hash] { TransferRow(hash: clip.hash, transfer: t).card() }
                content
                details
            }
            .padding()
        }
        .background(Color(.systemGroupedBackground))
        .navigationTitle(clip.look.name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if !clip.remote {
                ToolbarItem(placement: .topBarTrailing) {
                    if clip.kind == "text" {
                        ShareLink(item: clip.text)
                    } else if let p = clip.file ?? clip.paths.first {
                        ShareLink(item: URL(fileURLWithPath: p))
                    }
                }
            }
            ToolbarItem(placement: .topBarTrailing) {
                Button(role: .destructive) { confirmDelete = true } label: { Image(systemName: "trash") }.accessibilityLabel("Delete")
            }
        }
        .confirmationDialog("Delete this item from ClipBubble everywhere?", isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Delete", role: .destructive) { model.delete(clip); dismiss() }
        }
        .safeAreaInset(edge: .bottom) {
            Button { model.copy(clip) } label: {
                Label(clip.remote ? "Get from \(device)" : "Copy", systemImage: clip.remote ? "icloud.and.arrow.down" : "doc.on.doc")
                    .font(.body.weight(.semibold)).frame(maxWidth: .infinity, minHeight: 30)
            }
            .buttonStyle(.borderedProminent).buttonBorderShape(.capsule).controlSize(.large)
            .padding(.horizontal).padding(.bottom, 8)
        }
        .toast(model.line)
        // Reads the item back whenever history changes, so the screen updates when Get brings it over. The first read
        // also swaps the list's trimmed copy for the full item.
        .onReceive(model.$clips) { _ in live = model.clip(item.hash) ?? live }
    }

    private var device: String { clip.origin.isEmpty ? "This iPhone" : model.devices[clip.origin] ?? "computer" }

    @ViewBuilder private var content: some View {
        if let p = clip.imagePath, !clip.remote {
            ZoomableImage(path: p)
        } else if clip.kind == "text" {
            VStack(alignment: .leading, spacing: 12) {
                let shown = clip.text.prefix(Self.shownLength)
                Text(String(shown)).font(.body).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if shown.endIndex < clip.text.endIndex {
                    Text("Showing the start of it. Copy and Share take all of it.").font(.footnote).foregroundStyle(.secondary)
                }
                if clip.look == .link, let url = URL(string: clip.text.trimmingCharacters(in: .whitespacesAndNewlines)) {
                    Link(destination: url) { Label("Open link", systemImage: "safari") }.font(.body.weight(.medium))
                }
            }
            .card()
        } else {
            VStack(alignment: .leading, spacing: 10) {
                if clip.remote {
                    Label("Still on \(device). Get it to open or share it here.", systemImage: "icloud.and.arrow.down")
                        .foregroundStyle(.secondary)
                }
                ForEach(clip.kind == "image" ? [clip.file ?? "Image"] : clip.paths, id: \.self) { p in
                    HStack(spacing: 12) {
                        Image(systemName: "doc.fill").font(.title2).foregroundStyle(.orange)
                        VStack(alignment: .leading, spacing: 2) {
                            Text((p as NSString).lastPathComponent).font(.body).lineLimit(2)
                            if let size = fileSize(p) { Text(size).font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .card()
        }
    }

    private var details: some View {
        VStack(spacing: 0) {
            row("From", device)
            Divider()
            row("How", clip.source)
            Divider()
            row("When", clip.created.formatted(date: .abbreviated, time: .shortened))
            if clip.kind == "text" {
                Divider()
                row("Length", "\(clip.text.count) characters")
            }
        }
        .card(padding: 0)
    }

    private func row(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label).foregroundStyle(.secondary)
            Spacer()
            Text(value).multilineTextAlignment(.trailing)
        }
        .font(.subheadline)
        .padding(.horizontal, 16).padding(.vertical, 12)
    }

    private func fileSize(_ p: String) -> String? {
        guard let n = (try? FileManager.default.attributesOfItem(atPath: p)[.size] as? NSNumber)?.int64Value else { return nil }
        return ByteCountFormatter.string(fromByteCount: n, countStyle: .file)
    }
}

/// The full picture. Pinch to look closer; it settles back when you let go.
struct ZoomableImage: View {
    let path: String
    @State private var scale: CGFloat = 1

    var body: some View {
        Thumb(path, pixels: 2400).scaledToFit()
            .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            .scaleEffect(scale)
            .gesture(MagnifyGesture()
                .onChanged { scale = min(4, max(1, $0.magnification)) }
                .onEnded { _ in withAnimation(.snappy) { scale = 1 } })
            .zIndex(1)
    }
}

extension Clip {
    var names: String {
        kind == "image" ? "Image" : paths.map { ($0 as NSString).lastPathComponent }.joined(separator: ", ")
    }

    /// What VoiceOver reads for an item.
    var spoken: String {
        let what = kind == "text" ? String(text.prefix(300)) : kind == "image" ? "Image" : "File, \(names)"
        return remote ? "\(what), still on another device" : what
    }
}

/// A file on its way, with how far along it is and a way to stop it.
struct TransferRow: View {
    let hash: String
    let transfer: Transfer

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(transfer.sending ? "Sending to \(transfer.peer)" : "Getting from \(transfer.peer)")
                    Spacer()
                    Text(transfer.fraction, format: .percent.precision(.fractionLength(0))).monospacedDigit().foregroundStyle(.secondary)
                }
                .font(.subheadline)
                ProgressView(value: transfer.fraction)
            }
            .accessibilityElement(children: .combine)
            Button("Cancel") { SyncEngine.shared.cancelTransfer(hash) }.buttonStyle(.borderless).font(.subheadline)
        }
    }
}

private extension View {
    func card(padding: CGFloat = 16) -> some View {
        self.padding(padding)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }
}
