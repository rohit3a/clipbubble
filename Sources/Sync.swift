import Foundation

/// The device-sync model and wire format shared with the Linux and Windows apps (docs/sync-protocol.md).
/// History is a set of items keyed by hash. Each machine keeps, per hash, the latest event it knows: copied at `time`,
/// or deleted at `time`. The later event wins, so machines converge whatever order frames arrive in.
enum Sync {
    static let proto: UInt32 = 1
    static let maxFrame = 4 << 20
    static let chunkSize = 256 << 10
    /// Files and images up to this size move on their own; bigger ones wait for Get or Send to.
    static let autoFetchLimit: Int64 = 25 << 20
}

struct Stamp: Equatable {
    var hash: String
    var time: Double
    var deleted: Bool

    /// Later wins. On a tie, a delete wins, so taking something back never loses to a race.
    func beats(_ other: Stamp?) -> Bool {
        guard let other else { return true }
        return time > other.time || (time == other.time && deleted && !other.deleted)
    }
}

/// One file (or a copied image's PNG) that an item carries.
struct Blob: Equatable {
    var name: String
    var size: Int64
    var sha: String
}

struct Entry: Equatable {
    var hash: String
    var time: Double
    var kind: String
    /// Text for text items, file names for files, empty for images. Never local paths.
    var text: String
    var source: String
    /// The device that first copied it.
    var origin: String
    var blobs: [Blob]

    var stamp: Stamp { Stamp(hash: hash, time: time, deleted: false) }
    var size: Int64 { blobs.reduce(0) { $0 + $1.size } }
}

struct Hello: Equatable {
    var proto: UInt32
    var device: String
    var name: String
    var os: String
    var version: String
    var clock: Double

    /// Whether the peer reads `device` frames. Earlier versions close the link on a frame type they don't know.
    /// A build without a release number (a dev or test build) runs this code, so it does.
    var readsDevices: Bool {
        let parts = version.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard !parts.contains(nil) else { return true }
        return !parts.map { $0! }.lexicographicallyPrecedes([1, 6, 2])
    }
}

enum Frame: Equatable {
    case hello(Hello)
    /// Every stamp from the retention window, sent once after hello.
    case summary([Stamp])
    case put(Entry)
    case tombstone(Stamp)
    case fetch(hash: String, index: Int, offset: Int64)
    case chunk(hash: String, index: Int, offset: Int64, total: Int64, bytes: Data)
    case cancel(hash: String)
    /// The receiver put an item on its clipboard, or finished a file. Drives latency numbers and progress.
    case applied(hash: String)
    case ping(Double)
    case pong(Double)
    /// "Get this now": the sender wants the receiver to fetch an item, whatever its size.
    case offer(hash: String)
    /// A device the sender knows, maybe only through another link, so items relayed from it show its name.
    case device(id: String, name: String, os: String)

    var code: UInt8 {
        switch self {
        case .hello: 1
        case .summary: 2
        case .put: 3
        case .tombstone: 4
        case .fetch: 5
        case .chunk: 6
        case .cancel: 7
        case .applied: 8
        case .ping: 9
        case .pong: 10
        case .offer: 11
        case .device: 12
        }
    }

    /// File chunks queue behind everything else, so a copy never waits on a transfer.
    var isBulk: Bool { if case .chunk = self { true } else { false } }

    func encoded() -> Data {
        var w = Writer()
        w.u8(code)
        switch self {
        case .hello(let h):
            w.u32(h.proto); w.str(h.device); w.str(h.name); w.str(h.os); w.str(h.version); w.f64(h.clock)
        case .summary(let stamps):
            w.u32(UInt32(stamps.count))
            for s in stamps { w.stamp(s) }
        case .put(let e):
            w.str(e.hash); w.f64(e.time); w.str(e.kind); w.str(e.text); w.str(e.source); w.str(e.origin)
            w.u32(UInt32(e.blobs.count))
            for b in e.blobs { w.str(b.name); w.i64(b.size); w.str(b.sha) }
        case .tombstone(let s):
            w.stamp(s)
        case .fetch(let hash, let index, let offset):
            w.str(hash); w.u32(UInt32(index)); w.i64(offset)
        case .chunk(let hash, let index, let offset, let total, let bytes):
            w.str(hash); w.u32(UInt32(index)); w.i64(offset); w.i64(total); w.bytes(bytes)
        case .cancel(let hash), .applied(let hash), .offer(let hash):
            w.str(hash)
        case .ping(let t), .pong(let t):
            w.f64(t)
        case .device(let id, let name, let os):
            w.str(id); w.str(name); w.str(os)
        }
        var out = Data()
        out.reserveCapacity(w.data.count + 4)
        withUnsafeBytes(of: UInt32(w.data.count).bigEndian) { out.append(contentsOf: $0) }
        out.append(w.data)
        return out
    }

    /// Parses one frame body (type byte onward). Anything malformed throws; the link closes rather than guess.
    static func decode(_ body: Data) throws -> Frame {
        var r = Reader(body)
        let code = try r.u8()
        let f: Frame
        switch code {
        case 1:
            f = .hello(Hello(proto: try r.u32(), device: try r.str(), name: try r.str(), os: try r.str(),
                             version: try r.str(), clock: try r.f64()))
        case 2:
            let n = Int(try r.u32())
            guard n <= r.remaining / 13 else { throw SyncError.malformed }
            f = .summary(try (0..<n).map { _ in try r.stamp() })
        case 3:
            let hash = try r.str(), time = try r.f64(), kind = try r.str(), text = try r.str()
            let source = try r.str(), origin = try r.str()
            let n = Int(try r.u32())
            guard n <= r.remaining / 16 else { throw SyncError.malformed }
            let blobs = try (0..<n).map { _ in Blob(name: try r.str(), size: try r.i64(), sha: try r.str()) }
            guard ["text", "image", "file"].contains(kind), blobs.allSatisfy({ $0.size >= 0 }) else { throw SyncError.malformed }
            f = .put(Entry(hash: hash, time: time, kind: kind, text: text, source: source, origin: origin, blobs: blobs))
        case 4: f = .tombstone(try r.stamp())
        case 5: f = .fetch(hash: try r.str(), index: Int(try r.u32()), offset: try r.i64())
        case 6:
            f = .chunk(hash: try r.str(), index: Int(try r.u32()), offset: try r.i64(), total: try r.i64(), bytes: try r.bytes())
        case 7: f = .cancel(hash: try r.str())
        case 8: f = .applied(hash: try r.str())
        case 9: f = .ping(try r.f64())
        case 10: f = .pong(try r.f64())
        case 11: f = .offer(hash: try r.str())
        case 12: f = .device(id: try r.str(), name: try r.str(), os: try r.str())
        default: throw SyncError.malformed
        }
        guard r.remaining == 0 else { throw SyncError.malformed }
        return f
    }
}

enum SyncError: Error { case malformed, tooLarge }

/// Splits a byte stream into frames. Rejects an oversized length before buffering it.
struct FrameReader {
    private var buffer = Data()

    mutating func feed(_ d: Data) throws -> [Frame] {
        buffer.append(d)
        var out: [Frame] = []
        while buffer.count >= 4 {
            let start = buffer.startIndex
            let len = Int(buffer[start]) << 24 | Int(buffer[start + 1]) << 16 | Int(buffer[start + 2]) << 8 | Int(buffer[start + 3])
            guard len > 0, len <= Sync.maxFrame else { throw SyncError.tooLarge }
            guard buffer.count >= 4 + len else { break }
            out.append(try Frame.decode(buffer.subdata(in: start + 4 ..< start + 4 + len)))
            buffer.removeSubrange(start ..< start + 4 + len)
        }
        if buffer.isEmpty { buffer = Data() }
        return out
    }
}

enum Merge: Equatable { case ignore, insert, bump, delete }

/// What an incoming event does to this machine's history. `hasRow` says whether the item is here now.
func merge(local: Stamp?, hasRow: Bool, incoming: Stamp) -> Merge {
    guard incoming.beats(local) else { return .ignore }
    if incoming.deleted { return .delete }
    return hasRow ? .bump : .insert
}

/// What one side of a link sends after reading the other side's summary: every item or delete where it
/// knows a later event than the peer. Both sides run this, so each pushes only what it wins.
func reconcile(local: [Stamp], remote: [Stamp]) -> [Stamp] {
    let theirs = Dictionary(remote.map { ($0.hash, $0) }, uniquingKeysWith: { $0.beats($1) ? $0 : $1 })
    return local.filter { $0.beats(theirs[$0.hash]) }
}

// MARK: - Encoding

private struct Writer {
    var data = Data()
    mutating func u8(_ v: UInt8) { data.append(v) }
    mutating func u32(_ v: UInt32) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
    mutating func i64(_ v: Int64) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
    mutating func f64(_ v: Double) { withUnsafeBytes(of: v.bitPattern.bigEndian) { data.append(contentsOf: $0) } }
    mutating func bytes(_ d: Data) { u32(UInt32(d.count)); data.append(d) }
    mutating func str(_ s: String) { bytes(Data(s.utf8)) }
    mutating func stamp(_ s: Stamp) { str(s.hash); f64(s.time); u8(s.deleted ? 1 : 0) }
}

private struct Reader {
    private let data: Data
    private var at: Int
    init(_ d: Data) { data = d; at = d.startIndex }
    var remaining: Int { data.endIndex - at }

    private mutating func take(_ n: Int) throws -> Data {
        guard n >= 0, n <= remaining else { throw SyncError.malformed }
        defer { at += n }
        return data.subdata(in: at ..< at + n)
    }
    private mutating func int(_ n: Int) throws -> UInt64 { try take(n).reduce(0) { $0 << 8 | UInt64($1) } }

    mutating func u8() throws -> UInt8 { UInt8(try int(1)) }
    mutating func u32() throws -> UInt32 { UInt32(try int(4)) }
    mutating func i64() throws -> Int64 { Int64(bitPattern: try int(8)) }
    mutating func f64() throws -> Double {
        let v = Double(bitPattern: try int(8))
        guard v.isFinite else { throw SyncError.malformed }
        return v
    }
    mutating func bytes() throws -> Data { try take(Int(try u32())) }
    mutating func str() throws -> String {
        guard let s = String(data: try bytes(), encoding: .utf8) else { throw SyncError.malformed }
        return s
    }
    mutating func stamp() throws -> Stamp {
        let hash = try str(), time = try f64(), d = try u8()
        guard d <= 1 else { throw SyncError.malformed }
        return Stamp(hash: hash, time: time, deleted: d == 1)
    }
}
