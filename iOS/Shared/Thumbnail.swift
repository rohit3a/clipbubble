import ImageIO
import SwiftUI
import UIKit

/// Small copies of history images, made off the main thread and kept in memory. A full Mac screenshot decodes to
/// about 22 MB, which stalls scrolling and can push the keyboard past its memory limit.
enum Thumbnails {
    private static let cache = NSCache<NSString, UIImage>()

    static func cached(_ path: String, pixels: Int) -> UIImage? { cache.object(forKey: key(path, pixels)) }

    static func load(_ path: String, pixels: Int) async -> UIImage? {
        if let img = cached(path, pixels: pixels) { return img }
        let img = await Task.detached(priority: .userInitiated) { () -> UIImage? in
            let opts = [kCGImageSourceShouldCache: false] as CFDictionary
            guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, opts),
                  let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, [
                      kCGImageSourceCreateThumbnailFromImageAlways: true,
                      kCGImageSourceCreateThumbnailWithTransform: true,
                      kCGImageSourceShouldCacheImmediately: true,
                      kCGImageSourceThumbnailMaxPixelSize: pixels,
                  ] as CFDictionary)
            else { return nil }
            return UIImage(cgImage: cg)
        }.value
        if let img { cache.setObject(img, forKey: key(path, pixels)) }
        return img
    }

    private static func key(_ path: String, _ pixels: Int) -> NSString { "\(pixels):\(path)" as NSString }
}

/// An image from the history at thumbnail size, with a plain placeholder while it loads.
struct Thumb: View {
    let path: String
    let pixels: Int
    @State private var image: UIImage?

    init(_ path: String, pixels: Int) {
        self.path = path
        self.pixels = pixels
        _image = State(initialValue: Thumbnails.cached(path, pixels: pixels))
    }

    var body: some View {
        Group {
            if let image { Image(uiImage: image).resizable() } else { Color(.tertiarySystemFill) }
        }
        .task(id: path) { if image == nil { image = await Thumbnails.load(path, pixels: pixels) } }
    }
}
