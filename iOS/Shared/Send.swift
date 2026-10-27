import UIKit
import UniformTypeIdentifiers

/// What this iPhone adds to the history: the clipboard, the share sheet and Shortcuts. Each returns the item's hash.
enum Inbox {
    static func text(_ t: String, source: String) -> String? {
        guard !t.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        let hash = "t:" + sha(t)
        DB().upsert(kind: "text", text: t, file: nil, source: source, hash: hash)
        return hash
    }

    /// Stored as PNG, which computers check against the hash and paste as a picture.
    static func image(_ data: Data, source: String) -> String? {
        let png = data.starts(with: [0x89, 0x50, 0x4E, 0x47]) ? data : UIImage(data: data)?.pngData()
        guard let png else { return nil }
        let url = Paths.images.appendingPathComponent(sha(png) + ".png")
        try? FileManager.default.createDirectory(at: Paths.images, withIntermediateDirectories: true)
        guard (try? png.write(to: url)) != nil else { return nil }
        let hash = "i:" + sha(png)
        DB().upsert(kind: "image", text: "", file: url.path, source: source, hash: hash)
        return hash
    }

    /// A copy in ClipBubble's own folder, since what the share sheet hands over is gone once it closes.
    static func file(_ src: URL, source: String) -> String? {
        if src.pathExtension.lowercased() == "png", let d = try? Data(contentsOf: src), d.count < Int(Sync.autoFetchLimit) {
            return image(d, source: source)
        }
        let dir = Paths.dir.appendingPathComponent("sent").appendingPathComponent(UUID().uuidString)
        let dest = dir.appendingPathComponent(safeFileName(src.lastPathComponent))
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let scoped = src.startAccessingSecurityScopedResource()
        defer { if scoped { src.stopAccessingSecurityScopedResource() } }
        guard (try? FileManager.default.copyItem(at: src, to: dest)) != nil else { return nil }
        let hash = "f:" + sha(dest.path)
        DB().upsert(kind: "file", text: dest.path, file: nil, source: source, hash: hash)
        return hash
    }

    /// Text, a link, a picture or a file from anything that hands over item providers: Paste and the share sheet.
    /// Pasted pictures become images, so they paste as pictures on the computer; shared ones keep their file.
    static func add(_ providers: [NSItemProvider], source: String, picturesAsFiles: Bool = false) async -> [String] {
        var hashes: [String] = []
        for p in providers {
            if let h = await add(p, source: source, picturesAsFiles: picturesAsFiles) { hashes.append(h) }
        }
        return hashes
    }

    private static func add(_ p: NSItemProvider, source: String, picturesAsFiles: Bool) async -> String? {
        let types = p.registeredTypeIdentifiers.compactMap(UTType.init)
        if p.canLoadObject(ofClass: URL.self), let url = await object(p, URL.self) {
            return url.isFileURL ? file(url, source: source) : text(url.absoluteString, source: source)
        }
        if let t = types.first(where: { $0.conforms(to: .image) }) {
            if picturesAsFiles, let url = await fileCopy(p, t) { return file(url, source: source) }
            if let d = await data(p, t) { return image(d, source: source) }
        }
        if p.canLoadObject(ofClass: String.self), let s = await object(p, String.self) { return text(s, source: source) }
        if let t = types.first(where: { $0.conforms(to: .data) }), let url = await fileCopy(p, t) { return file(url, source: source) }
        return nil
    }

    private static func object<T: _ObjectiveCBridgeable>(_ p: NSItemProvider, _: T.Type) async -> T? where T._ObjectiveCType: NSItemProviderReading {
        await withCheckedContinuation { c in _ = p.loadObject(ofClass: T.self) { v, _ in c.resume(returning: v) } }
    }

    private static func data(_ p: NSItemProvider, _ t: UTType) async -> Data? {
        await withCheckedContinuation { c in _ = p.loadDataRepresentation(for: t) { d, _ in c.resume(returning: d) } }
    }

    /// The provider's file, moved somewhere that outlives its callback.
    private static func fileCopy(_ p: NSItemProvider, _ t: UTType) async -> URL? {
        await withCheckedContinuation { c in
            _ = p.loadFileRepresentation(for: t, openInPlace: false) { url, _, _ in
                guard let url else { return c.resume(returning: nil) }
                let keep = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
                try? FileManager.default.createDirectory(at: keep, withIntermediateDirectories: true)
                let dest = keep.appendingPathComponent(p.suggestedName.map { $0 + (url.pathExtension.isEmpty ? "" : "." + url.pathExtension) } ?? url.lastPathComponent)
                c.resume(returning: (try? FileManager.default.copyItem(at: url, to: dest)) != nil ? dest : nil)
            }
        }
    }
}

/// Links to the computers just long enough to hand something over, for the share sheet and Shortcuts, when
/// ClipBubble itself isn't open. Runs on the main thread.
@MainActor
enum Courier {
    /// Waits for a link, adds the items so they count as just copied, and returns once a computer has them on its
    /// clipboard or `timeout` passes. Whatever didn't make it still syncs the next time ClipBubble opens.
    static func deliver(timeout: Double = 25, status: @escaping (String) -> Void = { _ in },
                        add: @escaping () async -> [String]) async -> String {
        let engine = SyncEngine.shared
        guard !SyncEngine.enabledHosts.isEmpty else {
            return await add().isEmpty ? "Couldn't save that." : "Saved. Link a computer in ClipBubble to send it there."
        }
        engine.start()
        status("Connecting…")
        let linked = await until(seconds: 10) { engine.statuses.values.contains { $0.ok } }
        let hashes = await add()
        guard !hashes.isEmpty else { return "Couldn't save that." }
        guard linked else {
            let why = engine.statuses.values.first { !$0.ok }?.text ?? "Offline"
            return "Saved. \(why), so it goes over next time."
        }
        let names = engine.statuses.values.compactMap { s -> String? in if case .live(let h) = s { return h.name }; return nil }
        status("Sending to \(names.joined(separator: ", "))…")
        // Big files wait for Get on the computer; sending them from here means you want them there now.
        for h in hashes where h.hasPrefix("f:") { engine.linked.forEach { engine.send(h, toDevice: $0.device) } }
        // Each item and computer that confirmed it, so two of three photos arriving isn't reported as all of them.
        var got: Set<String> = []
        var progress: [String: Transfer] = [:]
        let (wasApplied, wasTransfers) = (engine.onApplied, engine.onTransfers)
        engine.onApplied = { hash, peer in wasApplied(hash, peer); if hashes.contains(hash) { got.insert(hash + "\n" + peer) } }
        engine.onTransfers = { t in
            wasTransfers(t)
            progress = t
            let sending = t.values.filter(\.sending)
            if !sending.isEmpty {
                let done = sending.map(\.done).reduce(0, +), total = max(1, sending.map(\.total).reduce(0, +))
                status("Sending… \(Int(Double(done) / Double(total) * 100))%")
            }
        }
        // A file still uploading gets up to ten more minutes.
        let deadline = Date().addingTimeInterval(timeout), hardStop = deadline.addingTimeInterval(600)
        while got.count < names.count * hashes.count, Date() < hardStop, Date() < deadline || progress.values.contains(where: \.sending) {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        engine.onApplied = wasApplied
        engine.onTransfers = wasTransfers
        // No answer yet doesn't mean it arrived: it's saved here and syncs again next time.
        if got.isEmpty { return "Saved. Still waiting for \(names.joined(separator: ", ")) to confirm." }
        let complete = names.filter { n in hashes.allSatisfy { got.contains($0 + "\n" + n) } }
        if complete.count == names.count { return "On the clipboard of \(complete.sorted().joined(separator: ", "))." }
        return "Saved. Some items are still on their way to \(names.joined(separator: ", "))."
    }

    private static func until(seconds: Double, _ cond: () -> Bool) async -> Bool {
        let end = Date().addingTimeInterval(seconds)
        while Date() < end {
            if cond() { return true }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        return cond()
    }
}
