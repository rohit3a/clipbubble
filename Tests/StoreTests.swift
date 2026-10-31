import Foundation
import SQLite3

func sweepTests() {
    let db = DB(), fm = FileManager.default
    let incoming = Paths.dir.appendingPathComponent("incoming")
    try? fm.createDirectory(at: incoming, withIntermediateDirectories: true)
    try? fm.createDirectory(at: Paths.images, withIntermediateDirectories: true)
    let kept = Paths.images.appendingPathComponent("kept.png"), leftover = Paths.images.appendingPathComponent("leftover.png")
    let recent = Paths.images.appendingPathComponent("recent.png"), partial = incoming.appendingPathComponent("partial-0")
    db.upsert(kind: "image", text: "", file: kept.path, source: "Preview", hash: "i:kept")
    let old = Date().addingTimeInterval(-30 * 86400)
    for f in [kept, leftover, partial, recent] {
        try? Data("x".utf8).write(to: f)
        if f != recent { try? fm.setAttributes([.modificationDate: old], ofItemAtPath: f.path) }
    }
    db.prune()
    check(!fm.fileExists(atPath: leftover.path), "prune clears an old image with no row")
    check(!fm.fileExists(atPath: partial.path), "and an old partial download")
    check(fm.fileExists(atPath: kept.path), "an image with a row stays")
    check(fm.fileExists(atPath: recent.path), "and one too recent to judge")
    try? fm.removeItem(at: recent)
    db.delete(hash: "i:kept")
    try? Data("x".utf8).write(to: partial)
    db.deleteAll()
    check(!fm.fileExists(atPath: incoming.path), "Clear all takes partial downloads too")
}

func storeTests() {
    // A 1.3.0 database upgrades in place and keeps every row.
    try? FileManager.default.createDirectory(at: Paths.dir, withIntermediateDirectories: true)
    var old: OpaquePointer?
    sqlite3_open(Paths.db.path, &old)
    let now = Date().timeIntervalSince1970
    sqlite3_exec(old, """
        CREATE TABLE clips(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
          file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL);
        CREATE TABLE forgotten(hash TEXT PRIMARY KEY, at REAL NOT NULL);
        INSERT INTO clips(kind,text,source,hash,created) VALUES('text','old one','Notes','t:old1',\(now - 60));
        INSERT INTO clips(kind,text,source,hash,created) VALUES('file','/tmp/x.png','Screenshot','f:old2',\(now - 30));
        """, nil, nil, nil)
    sqlite3_close(old)

    let db = DB()
    let clips = db.clips()
    check(clips.map(\.hash) == ["f:old2", "t:old1"], "1.3.0 rows survive the upgrade")
    check(clips.allSatisfy { $0.origin == "" && !$0.remote }, "upgraded rows are local")
    _ = DB()
    check(db.clips().count == 2, "opening twice doesn't migrate twice")

    var seen: [String] = []
    DB.changed = { seen += $0 }

    // A deleted screenshot isn't re-imported, but a newer copy of it is.
    let shot = db.clips().first { $0.hash == "f:old2" }!
    db.delete(id: shot.id)
    check(seen == ["f:old2"], "delete reports its hash")
    db.insertIfMissing(kind: "file", text: "/tmp/x.png", source: "Screenshot", hash: "f:old2", created: Date(timeIntervalSince1970: now - 30))
    check(db.clip(hash: "f:old2") == nil, "deleted screenshot stays deleted")
    check(seen == ["f:old2"], "a refused import reports nothing")
    db.upsert(kind: "file", text: "/tmp/x.png", file: nil, source: "Finder", hash: "f:old2")
    check(db.clip(hash: "f:old2") != nil, "copying it again brings it back")

    // On a computer, deleting a copied file removes the item, never the file, even one inside ClipBubble's folder.
    let mine = Paths.dir.appendingPathComponent("notes.txt")
    FileManager.default.createFile(atPath: mine.path, contents: Data("keep".utf8))
    db.upsert(kind: "file", text: mine.path, file: nil, source: "Finder", hash: "f:mine")
    db.delete(hash: "f:mine")
    check(FileManager.default.fileExists(atPath: mine.path), "deleting a copied file keeps the file")

    // Stamps carry the latest event per hash, and a delete outranks the copy it removed.
    db.delete(hash: "t:old1")
    let stamps = Dictionary(uniqueKeysWithValues: db.stamps().map { ($0.hash, $0) })
    check(stamps["t:old1"]?.deleted == true, "deleted text shows as a delete")
    check(stamps["f:old2"]?.deleted == false, "recopied file shows as present")
    check(db.stamp(hash: "t:old1")?.deleted == true, "single stamp lookup agrees")

    // Items from another device: insert, bump, delete, and a later recopy wins over that delete.
    seen = []
    let e = Entry(hash: "t:rog", time: now - 5, kind: "text", text: "from rog", source: "Chrome", origin: "rog-id", blobs: [])
    db.insert(e, origin: "rog-id")
    check(db.clip(hash: "t:rog")?.origin == "rog-id", "synced item keeps its origin")
    db.bump(hash: "t:rog", to: now - 1)
    check(db.stamp(hash: "t:rog")?.time == now - 1, "bump moves the time")
    db.tombstone(Stamp(hash: "t:rog", time: now - 3, deleted: true))
    check(db.clip(hash: "t:rog") != nil, "an older delete doesn't remove a newer copy")
    db.tombstone(Stamp(hash: "t:rog", time: now, deleted: true))
    check(db.clip(hash: "t:rog") == nil, "a newer delete removes it")
    check(seen == ["t:rog", "t:rog", "t:rog", "t:rog"], "every sync write reports its hash")

    let img = Entry(hash: "i:abc", time: now, kind: "image", text: "", source: "Figma", origin: "rog-id",
                    blobs: [Blob(name: "abc.png", size: 10, sha: "abc")])
    db.insert(img, origin: "rog-id")
    check(db.clip(hash: "i:abc")?.remote == true, "image waits for its bytes")
    db.received(hash: "i:abc", file: "/tmp/abc.png", text: nil)
    check(db.clip(hash: "i:abc")?.remote == false && db.clip(hash: "i:abc")?.file == "/tmp/abc.png", "image lands")

    // Kinds switched off in Settings are neither offered nor accepted.
    check(db.allowed(kind: "image", source: "Figma"), "images allowed by default")
    Prefs.set("images", false)
    check(!db.allowed(kind: "image", source: "Figma"), "images refused when switched off")
    check(!db.stamps().contains { $0.hash == "i:abc" }, "hidden items aren't offered")
    Prefs.set("images", true)
    UserDefaults.standard.removeObject(forKey: "save.images")

    check(db.saveDevice(id: "rog-id", name: "ROG", os: "linux"), "a new device is news")
    check(!db.saveDevice(id: "rog-id", name: "ROG", os: "linux"), "the same device again isn't")
    check(db.saveDevice(id: "rog-id", name: "ROG 2", os: "linux"), "a new name is news")
    check(db.deviceNames()["rog-id"] == "ROG 2", "device names update")
    check(db.devices().contains { $0 == ("rog-id", "ROG 2", "linux") }, "devices come back with their OS")
    DB.changed = nil
}
