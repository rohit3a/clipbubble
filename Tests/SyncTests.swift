import Foundation

/// One frame of every type, with awkward values: the Linux and Windows apps must read and write these byte for byte.
let fixtureFrames: [(String, Frame)] = [
    ("hello", .hello(Hello(proto: 1, device: "3F2A-mac", name: "Rohit's MacBook", os: "macos", version: "1.4.0", clock: 1792000000.25))),
    ("summary", .summary([Stamp(hash: "t:abc", time: 1792000000.5, deleted: false), Stamp(hash: "i:def", time: 1791999999, deleted: true)])),
    ("summary-empty", .summary([])),
    ("put-text", .put(Entry(hash: "t:01", time: 1792000001, kind: "text", text: "héllo\nwörld 👋", source: "Safari", origin: "3F2A-mac", blobs: []))),
    ("put-image", .put(Entry(hash: "i:ff", time: 1792000002, kind: "image", text: "", source: "Figma", origin: "rog",
                             blobs: [Blob(name: "ff.png", size: 1234, sha: "ff")]))),
    ("put-files", .put(Entry(hash: "f:9", time: 1792000003, kind: "file", text: "a.pdf\nb c.zip", source: "Finder", origin: "3F2A-mac",
                             blobs: [Blob(name: "a.pdf", size: 10, sha: ""), Blob(name: "b c.zip", size: 30_000_000_000, sha: "")]))),
    ("tombstone", .tombstone(Stamp(hash: "t:01", time: 1792000004, deleted: true))),
    ("fetch", .fetch(hash: "f:9", index: 1, offset: 262144)),
    ("chunk", .chunk(hash: "f:9", index: 0, offset: 0, total: 10, bytes: Data([0, 1, 2, 255, 254, 10, 13, 0, 7, 9]))),
    ("cancel", .cancel(hash: "f:9")),
    ("applied", .applied(hash: "t:01")),
    ("ping", .ping(1792000005.125)),
    ("pong", .pong(1792000005.125)),
]

func hex(_ d: Data) -> String { d.map { String(format: "%02x", $0) }.joined() }

func writeFixtures(to url: URL) {
    let rows = fixtureFrames.map { ["name": $0.0, "hex": hex($0.1.encoded())] }
    let data = try! JSONSerialization.data(withJSONObject: rows, options: [.prettyPrinted, .sortedKeys])
    try! data.write(to: url)
}

func syncTests() {
    // Every frame survives a round trip, and the encoding matches the committed fixtures exactly.
    let fixtureURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("fixtures/sync/frames.json")
    let committed = (try? JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL))) as? [[String: String]] ?? []
    check(committed.count == fixtureFrames.count, "fixtures file has every frame")
    for (name, frame) in fixtureFrames {
        let bytes = frame.encoded()
        var reader = FrameReader()
        check((try? reader.feed(bytes)) == [frame], "round trip \(name)")
        check(committed.first { $0["name"] == name }?["hex"] == hex(bytes), "fixture bytes \(name)")
    }

    // Frames split across reads, and several in one read.
    let all = fixtureFrames.map { $0.1.encoded() }.reduce(Data(), +)
    var reader = FrameReader()
    var got: [Frame] = []
    var i = all.startIndex
    while i < all.endIndex {
        let n = min(7, all.endIndex - i)
        got += (try? reader.feed(all.subdata(in: i ..< i + n))) ?? []
        i += n
    }
    check(got == fixtureFrames.map { $0.1 }, "frames reassemble from 7-byte reads")

    // Oversized lengths are refused before buffering; truncated or padded bodies are malformed.
    var big = FrameReader()
    check((try? big.feed(Data([0x7f, 0xff, 0xff, 0xff]))) == nil, "rejects a 2 GB frame length")
    var zero = FrameReader()
    check((try? zero.feed(Data([0, 0, 0, 0]))) == nil, "rejects an empty frame")
    let ping = Frame.ping(1).encoded()
    check((try? Frame.decode(ping.dropFirst(4).dropLast())) == nil, "rejects a truncated body")
    check((try? Frame.decode(ping.dropFirst(4) + Data([0]))) == nil, "rejects trailing bytes")
    check((try? Frame.decode(Data([99]))) == nil, "rejects an unknown type")
    let nan = Data([9]) + withUnsafeBytes(of: Double.nan.bitPattern.bigEndian) { Data($0) }
    check((try? Frame.decode(nan)) == nil, "rejects a NaN time")
    let hugeCount = Data([2, 0xff, 0xff, 0xff, 0xff])
    check((try? Frame.decode(hugeCount)) == nil, "rejects a summary count larger than the frame")

    // Random bytes never crash the decoder.
    var rng = SystemRandomNumberGenerator()
    for _ in 0..<3000 {
        let n = Int.random(in: 1...64, using: &rng)
        let body = Data((0..<n).map { _ in UInt8.random(in: 0...255, using: &rng) })
        _ = try? Frame.decode(body)
    }
    for (_, frame) in fixtureFrames {
        let body = Data(frame.encoded().dropFirst(4))
        for _ in 0..<50 {
            var mutated = body
            mutated[Int.random(in: 0..<mutated.count, using: &rng)] = UInt8.random(in: 0...255, using: &rng)
            _ = try? Frame.decode(mutated)
            _ = try? Frame.decode(mutated.prefix(Int.random(in: 0..<mutated.count, using: &rng)))
        }
    }
    check(true, "fuzzed 3,650 bodies without a crash")

    // Last writer wins, deletes win ties.
    let a = Stamp(hash: "h", time: 10, deleted: false)
    check(Stamp(hash: "h", time: 11, deleted: false).beats(a), "later copy wins")
    check(!Stamp(hash: "h", time: 9, deleted: true).beats(a), "earlier delete loses")
    check(Stamp(hash: "h", time: 10, deleted: true).beats(a), "delete wins a tie")
    check(!a.beats(Stamp(hash: "h", time: 10, deleted: true)), "copy loses a tie to delete")
    check(!a.beats(a), "an event doesn't beat itself")

    check(merge(local: nil, hasRow: false, incoming: a) == .insert, "new item inserts")
    check(merge(local: a, hasRow: true, incoming: Stamp(hash: "h", time: 12, deleted: false)) == .bump, "recopy bumps")
    check(merge(local: a, hasRow: true, incoming: a) == .ignore, "same event is ignored")
    check(merge(local: a, hasRow: true, incoming: Stamp(hash: "h", time: 12, deleted: true)) == .delete, "later delete deletes")
    check(merge(local: Stamp(hash: "h", time: 20, deleted: true), hasRow: false, incoming: a) == .ignore, "older copy stays deleted")
    check(merge(local: Stamp(hash: "h", time: 5, deleted: true), hasRow: false, incoming: a) == .insert, "copy after delete comes back")

    // Any order of the same events ends in the same history, and replaying them changes nothing.
    for _ in 0..<500 {
        let events = (0..<12).map { _ in
            Stamp(hash: "h\(Int.random(in: 0..<4))", time: Double(Int.random(in: 0..<8)), deleted: Bool.random())
        }
        let one = applyAll(events), two = applyAll(events.shuffled())
        check(one == two, "order-independent: \(events)")
        check(applyAll(events + events) == one, "replay-safe")
    }

    // Reconcile sends only what this side wins.
    let mine = [Stamp(hash: "a", time: 5, deleted: false), Stamp(hash: "b", time: 5, deleted: false),
                Stamp(hash: "c", time: 9, deleted: true), Stamp(hash: "d", time: 1, deleted: false)]
    let theirs = [Stamp(hash: "a", time: 5, deleted: false), Stamp(hash: "b", time: 6, deleted: false),
                  Stamp(hash: "c", time: 3, deleted: false), Stamp(hash: "e", time: 1, deleted: false)]
    check(Set(reconcile(local: mine, remote: theirs).map(\.hash)) == ["c", "d"], "reconcile sends c and d")
    check(Set(reconcile(local: theirs, remote: mine).map(\.hash)) == ["b", "e"], "other side sends b and e")
    let both = applyAll(mine + reconcile(local: theirs, remote: mine))
    check(both == applyAll(theirs + reconcile(local: mine, remote: theirs)), "one reconcile converges both sides")
}

/// Plays events through `merge` into a tiny in-memory history: hash to winning stamp, plus whether a row is present.
private func applyAll(_ events: [Stamp]) -> [String: Stamp] {
    var state: [String: Stamp] = [:]
    for e in events {
        switch merge(local: state[e.hash], hasRow: state[e.hash].map { !$0.deleted } ?? false, incoming: e) {
        case .ignore: break
        case .insert, .bump, .delete: state[e.hash] = e
        }
    }
    return state
}
