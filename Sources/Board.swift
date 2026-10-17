import AppKit

/// Writes clips back to the macOS clipboard.
enum Board {
    static func pngData(_ path: String) -> Data? {
        if (path as NSString).pathExtension.lowercased() == "png" { return FileManager.default.contents(atPath: path) }
        return NSImage(contentsOfFile: path)?.tiffRepresentation
            .flatMap { NSBitmapImageRep(data: $0) }?.representation(using: .png, properties: [:])
    }

    /// Puts a clip on the clipboard. Returns false, leaving the clipboard alone, when its image or files are gone,
    /// so a paste never lands whatever was copied before.
    @discardableResult
    static func write(_ c: Clip) -> Bool {
        switch c.kind {
        case "text":
            writeText(c.text)
            return true
        case "image":
            guard let f = c.file, let d = pngData(f) else { return false }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setData(d, forType: .png)
            return true
        default:
            let urls = c.paths.filter { FileManager.default.fileExists(atPath: $0) }.map { URL(fileURLWithPath: $0) }
            guard !urls.isEmpty else { return false }
            writeFiles(urls)
            return true
        }
    }

    /// `transient` marks it so clipboard managers (ClipBubble included) don't save it.
    static func writeText(_ s: String, transient: Bool = false) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(s, forType: .string)
        if transient { NSPasteboard.general.setString("", forType: .init("org.nspasteboard.TransientType")) }
    }

    /// A single image file goes on as both the file and its pixels, so it pastes into Finder and into chat/web apps.
    static func writeFiles(_ urls: [URL]) {
        let pb = NSPasteboard.general
        pb.clearContents()
        if urls.count == 1, isImagePath(urls[0].path), let d = pngData(urls[0].path) {
            let item = NSPasteboardItem()
            item.setString(urls[0].absoluteString, forType: .fileURL)
            item.setData(d, forType: .png)
            pb.writeObjects([item])
        } else {
            pb.writeObjects(urls as [NSURL])
        }
    }
}
