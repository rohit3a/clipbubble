import UIKit
import UniformTypeIdentifiers

/// What the shared sync engine needs from iOS. The app, the share extension and the keyboard all use one App Group,
/// so they share the history, the linked computers and the SSH key.
enum Platform {
    static let group = "group.studio.krnl.clipbubble"
    static let os = "ios"
    static var deviceName: String { UIDevice.current.name }
    static let dataDir = (FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group)
        ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]).appendingPathComponent("ClipBubble")
    static let defaults = UserDefaults(suiteName: group) ?? .standard
    /// The app is only ever linking while you're looking at it, so it never waits long.
    static let retryCap: Double = 30
    static let hostChanged = "This computer's identity changed. Link it again."
    static let keyRefused = "This computer doesn't know this iPhone. Link it again."
    /// Files from other devices; open them from ClipBubble to save or share.
    static var downloads: URL { dataDir }

    /// Something you copied seconds ago on another device goes straight onto this iPhone's clipboard.
    static func apply(_ c: Clip) {
        DispatchQueue.main.async { Clipboard.write(c) }
    }

    /// Nothing dials into an iPhone: it always starts the link.
    static func listen(_ engine: SyncEngine) -> AnyObject? { nil }

    /// Coming back to the app is the iPhone's wake: links dropped in the background redial at once.
    static func onWake(_ f: @escaping () -> Void) {
        NotificationCenter.default.addObserver(forName: UIApplication.willEnterForegroundNotification, object: nil, queue: nil) { _ in f() }
    }

    static func dial(_ alias: String, _ command: String, stderr: @escaping (Data) -> Void, exited: @escaping () -> Void) throws -> Tunnel {
        try SSHTunnel(alias: alias, command: command, stderr: stderr, exited: exited)
    }
}

enum Clipboard {
    @discardableResult
    static func write(_ c: Clip) -> Bool {
        switch c.kind {
        case "text":
            UIPasteboard.general.string = c.text
        case "image":
            guard let f = c.file, writeImage(f) else { return false }
        default:
            let urls = c.paths.map { URL(fileURLWithPath: $0) }.filter { FileManager.default.fileExists(atPath: $0.path) }
            guard !urls.isEmpty else { return false }
            if urls.count != 1 || !isImagePath(urls[0].path) || !writeImage(urls[0].path) {
                // Paste lands the file itself in Mail, Messages and Files.
                UIPasteboard.general.itemProviders = urls.compactMap { NSItemProvider(contentsOf: $0) }
            }
        }
        return true
    }

    /// The file's own bytes under its type: no decode and re-encode, so a big screenshot copies at once.
    private static func writeImage(_ path: String) -> Bool {
        guard let type = UTType(filenameExtension: (path as NSString).pathExtension), type.conforms(to: .image),
              let data = FileManager.default.contents(atPath: path)
        else { return false }
        UIPasteboard.general.setData(data, forPasteboardType: type.identifier)
        return true
    }
}
