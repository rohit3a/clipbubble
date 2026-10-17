import AppKit
import CryptoKit
import SQLite3

/// One clipboard entry. For files, `text` holds newline-separated paths.
struct Clip: Identifiable, Hashable {
    let id: Int64
    let kind: String          // "text" | "image" | "file"
    let text: String
    let file: String?         // stored PNG for images
    let source: String        // app name, or "iPhone" for Universal Clipboard
    let created: Date
    var hash = ""
    var origin = ""           // device id it was synced from; empty when copied on this machine
    var remote = false        // its image or files haven't arrived from that device yet

    var paths: [String] { kind == "file" ? text.split(separator: "\n").map(String.init) : [] }
    var imagePath: String? { kind == "image" ? file : paths.first(where: isImagePath) }
}

let imageExts: Set<String> = ["png", "jpg", "jpeg", "heic", "tiff", "tif", "gif", "webp", "bmp"]
func isImagePath(_ p: String) -> Bool { imageExts.contains((p as NSString).pathExtension.lowercased()) }

func sha(_ d: Data) -> String { SHA256.hash(data: d).map { String(format: "%02x", $0) }.joined() }
func sha(_ s: String) -> String { sha(Data(s.utf8)) }

enum Paths {
    /// `CLIPBUBBLE_HOME` points tests and a second local instance at their own data.
    static let dir = ProcessInfo.processInfo.environment["CLIPBUBBLE_HOME"].map { URL(fileURLWithPath: $0) }
        ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/ClipBubble")
    static let images = dir.appendingPathComponent("images")
    static let db = dir.appendingPathComponent("clips.sqlite")
}

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

/// SQLite store shared by the app (writer) and the MCP server (reader).
final class DB {
    static let retention: TimeInterval = 7 * 24 * 3600
    /// Called with the hashes of every insert, bump or delete, from any DB in this process. Sync sets it;
    /// it stays nil (and costs nothing) when no device is linked.
    static var changed: (([String]) -> Void)?
    private var h: OpaquePointer?

    init() {
        // Clipboard history is private: keep the folder readable by this user only.
        try? FileManager.default.createDirectory(at: Paths.images, withIntermediateDirectories: true)
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: Paths.dir.path)
        sqlite3_open(Paths.db.path, &h)
        sqlite3_busy_timeout(h, 3000)
        // `forgotten` holds deletes with their time. A clip is only kept if it was copied after its delete,
        // so a deleted screenshot isn't re-imported and a delete on another device sticks.
        sqlite3_exec(h, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS clips(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
              file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS clips_created ON clips(created);
            CREATE TABLE IF NOT EXISTS forgotten(hash TEXT PRIMARY KEY, at REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, os TEXT NOT NULL DEFAULT '');
            """, nil, nil, nil)
        if scalar("PRAGMA user_version") < 1 {
            sqlite3_exec(h, """
                BEGIN;
                ALTER TABLE clips ADD COLUMN origin TEXT NOT NULL DEFAULT '';
                ALTER TABLE clips ADD COLUMN remote INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 1;
                COMMIT;
                """, nil, nil, nil)
        }
    }

    private func scalar(_ sql: String) -> Int {
        let s = prepare(sql, [])
        defer { sqlite3_finalize(s) }
        return sqlite3_step(s) == SQLITE_ROW ? Int(sqlite3_column_int64(s, 0)) : 0
    }

    private func prepare(_ sql: String, _ args: [Any?]) -> OpaquePointer? {
        var s: OpaquePointer?
        guard sqlite3_prepare_v2(h, sql, -1, &s, nil) == SQLITE_OK else { return nil }
        for (i, a) in args.enumerated() {
            let idx = Int32(i + 1)
            switch a {
            case let v as String: sqlite3_bind_text(s, idx, v, -1, SQLITE_TRANSIENT)
            case let v as Double: sqlite3_bind_double(s, idx, v)
            case let v as Int64: sqlite3_bind_int64(s, idx, v)
            case let v as Int: sqlite3_bind_int64(s, idx, Int64(v))
            default: sqlite3_bind_null(s, idx)
            }
        }
        return s
    }

    private func run(_ sql: String, _ args: Any?...) {
        let s = prepare(sql, args)
        sqlite3_step(s)
        sqlite3_finalize(s)
    }

    private func select(_ tail: String, _ args: [Any?]) -> [Clip] {
        let s = prepare("SELECT id,kind,text,file,source,created,hash,origin,remote FROM clips " + tail, args)
        defer { sqlite3_finalize(s) }
        func col(_ i: Int32) -> String { sqlite3_column_text(s, i).map { String(cString: $0) } ?? "" }
        var out: [Clip] = []
        while sqlite3_step(s) == SQLITE_ROW {
            out.append(Clip(id: sqlite3_column_int64(s, 0), kind: col(1), text: col(2),
                            file: sqlite3_column_type(s, 3) == SQLITE_NULL ? nil : col(3),
                            source: col(4), created: Date(timeIntervalSince1970: sqlite3_column_double(s, 5)),
                            hash: col(6), origin: col(7), remote: sqlite3_column_int(s, 8) != 0))
        }
        return out
    }

    /// Inserts a clip, or moves an identical existing one to the top.
    func upsert(kind: String, text: String, file: String?, source: String, hash: String) {
        run("""
            INSERT INTO clips(kind,text,file,source,hash,created) VALUES(?,?,?,?,?,?)
            ON CONFLICT(hash) DO UPDATE SET created=excluded.created,
              source=CASE WHEN clips.source IN ('Screenshot', 'Downloads') THEN clips.source ELSE excluded.source END
            """, kind, text, file, source, hash, Date().timeIntervalSince1970)
        DB.changed?([hash])
    }

    /// Adds a clip with its own timestamp, leaving any existing identical clip untouched,
    /// unless it was deleted after that time (a screenshot or download you removed stays removed).
    func insertIfMissing(kind: String, text: String, source: String, hash: String, created: Date) {
        run("""
            INSERT OR IGNORE INTO clips(kind,text,source,hash,created)
            SELECT ?,?,?,?,? WHERE NOT EXISTS (SELECT 1 FROM forgotten WHERE hash = ? AND at >= ?)
            """, kind, text, source, hash, created.timeIntervalSince1970, hash, created.timeIntervalSince1970)
        if DB.changed != nil, sqlite3_changes(h) > 0 { DB.changed?([hash]) }
    }

    func clips(limit: Int = 300, kind: String? = nil, source: String? = nil,
               query: String? = nil, sinceHours: Double? = nil) -> [Clip] {
        var tail = "WHERE created >= ?"
        var args: [Any?] = [Date().timeIntervalSince1970 - (sinceHours.map { $0 * 3600 } ?? Self.retention)]
        if kind == "image" {
            // Image files (like screenshots) count as images too.
            tail += " AND (kind = 'image' OR (kind = 'file' AND (" +
                imageExts.map { "text LIKE '%.\($0)'" }.joined(separator: " OR ") + ")))"
        } else if let kind {
            tail += " AND kind = ?"
            args.append(kind)
        }
        if let source {
            tail += " AND (source = ? COLLATE NOCASE OR origin IN (SELECT id FROM devices WHERE name = ? COLLATE NOCASE))"
            args += [source, source]
        }
        if let query, !query.isEmpty {
            // Match % and _ literally: search is plain text, not a pattern.
            let q = "%" + query.replacingOccurrences(of: "\\", with: "\\\\")
                .replacingOccurrences(of: "%", with: "\\%").replacingOccurrences(of: "_", with: "\\_") + "%"
            tail += " AND (text LIKE ? ESCAPE '\\' OR source LIKE ? ESCAPE '\\')"
            args += [q, q]
        }
        tail += Prefs.hiddenSQL
        tail += " ORDER BY created DESC LIMIT ?"
        args.append(limit)
        return select(tail, args)
    }

    /// One clip by id. Items in categories switched off in Settings stay hidden here too (agents use this).
    func clip(id: Int64) -> Clip? { select("WHERE id = ?" + Prefs.hiddenSQL, [id]).first }

    func exists(hash: String) -> Bool { !select("WHERE hash = ?", [hash]).isEmpty }

    /// Removes a clip the app captured itself (a paste-through). Recorded as a delete so linked devices drop it too.
    func delete(hash: String) {
        let rows = select("WHERE hash = ?", [hash])
        forget(rows)
        removeFiles(rows)
        run("DELETE FROM clips WHERE hash = ?", hash)
        if !rows.isEmpty { DB.changed?([hash]) }
    }

    /// Only images ClipBubble stored itself are deleted; screenshots, downloads and copied files belong to the user.
    private func removeFiles(_ clips: [Clip]) {
        for c in clips {
            guard let f = c.file, (f as NSString).deletingLastPathComponent == Paths.images.path else { continue }
            try? FileManager.default.removeItem(atPath: f)
        }
    }

    /// Records each delete with its time: folders re-import, and linked devices need to hear about it.
    private func forget(_ clips: [Clip], at: Double = Date().timeIntervalSince1970) {
        guard !clips.isEmpty else { return }
        run("BEGIN")
        for c in clips { run("INSERT OR REPLACE INTO forgotten(hash, at) SELECT hash, ? FROM clips WHERE id = ?", at, c.id) }
        run("COMMIT")
    }

    func delete(id: Int64) {
        let rows = select("WHERE id = ?", [id])
        forget(rows)
        removeFiles(rows)
        run("DELETE FROM clips WHERE id = ?", id)
        if !rows.isEmpty { DB.changed?(rows.map(\.hash)) }
    }

    func deleteAll() {
        let rows = select("", [])
        forget(rows)
        removeFiles(rows)
        run("DELETE FROM clips")
        if !rows.isEmpty { DB.changed?(rows.map(\.hash)) }
    }

    // MARK: Sync

    /// The winning event per hash over the retention window: copies (left out when their kind is switched off) and deletes.
    /// `includingWaiting: false` leaves out synced items whose bytes haven't arrived, so peers offer them again.
    func stamps(includingWaiting: Bool = true) -> [Stamp] {
        let cutoff = Date().timeIntervalSince1970 - Self.retention
        var out: [String: Stamp] = [:]
        let s = prepare("""
            SELECT hash, created, 0 FROM clips WHERE created >= ?\(includingWaiting ? "" : " AND remote = 0")\(Prefs.hiddenSQL)
            UNION ALL SELECT hash, at, 1 FROM forgotten WHERE at >= ?
            """, [cutoff, cutoff])
        defer { sqlite3_finalize(s) }
        while sqlite3_step(s) == SQLITE_ROW {
            let st = Stamp(hash: String(cString: sqlite3_column_text(s, 0)), time: sqlite3_column_double(s, 1),
                           deleted: sqlite3_column_int(s, 2) == 1)
            if st.beats(out[st.hash]) { out[st.hash] = st }
        }
        return Array(out.values)
    }

    func stamp(hash: String) -> Stamp? {
        let s = prepare("SELECT created, 0 FROM clips WHERE hash = ? UNION ALL SELECT at, 1 FROM forgotten WHERE hash = ?", [hash, hash])
        defer { sqlite3_finalize(s) }
        var best: Stamp?
        while sqlite3_step(s) == SQLITE_ROW {
            let st = Stamp(hash: hash, time: sqlite3_column_double(s, 0), deleted: sqlite3_column_int(s, 1) == 1)
            if st.beats(best) { best = st }
        }
        return best
    }

    func clip(hash: String) -> Clip? { select("WHERE hash = ?", [hash]).first }

    /// Whether a kind of item from a source would be shown, under the Settings switches.
    func allowed(kind: String, source: String) -> Bool {
        let s = prepare("SELECT 1 FROM (SELECT ? AS kind, ? AS source) WHERE 1" + Prefs.hiddenSQL, [kind, source])
        defer { sqlite3_finalize(s) }
        return sqlite3_step(s) == SQLITE_ROW
    }

    /// Adds an item from a linked device. Its image or files are marked remote until they arrive.
    func insert(_ e: Entry, origin: String) {
        let names = e.kind == "file" ? e.text : ""
        run("""
            INSERT INTO clips(kind,text,source,hash,created,origin,remote) VALUES(?,?,?,?,?,?,?)
            ON CONFLICT(hash) DO UPDATE SET created = max(created, excluded.created)
            """, e.kind, e.kind == "text" ? e.text : names, e.source, e.hash, e.time, origin, e.blobs.isEmpty ? 0 : 1)
        DB.changed?([e.hash])
    }

    func bump(hash: String, to time: Double) {
        run("UPDATE clips SET created = ? WHERE hash = ? AND created < ?", time, hash, time)
        DB.changed?([hash])
    }

    /// A delete from a linked device: removes the item unless it was copied again after that.
    func tombstone(_ st: Stamp) {
        let rows = select("WHERE hash = ? AND created <= ?", [st.hash, st.time])
        removeFiles(rows)
        run("DELETE FROM clips WHERE hash = ? AND created <= ?", st.hash, st.time)
        run("INSERT INTO forgotten(hash, at) VALUES(?, ?) ON CONFLICT(hash) DO UPDATE SET at = max(at, excluded.at)", st.hash, st.time)
        DB.changed?([st.hash])
    }

    /// An image or files arrived: point the item at them and make it pasteable.
    func received(hash: String, file: String?, text: String?) {
        if let text { run("UPDATE clips SET remote = 0, text = ? WHERE hash = ?", text, hash) }
        else { run("UPDATE clips SET remote = 0, file = ? WHERE hash = ?", file, hash) }
    }

    func saveDevice(id: String, name: String, os: String) {
        run("INSERT INTO devices(id,name,os) VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name, os=excluded.os", id, name, os)
    }

    func deviceNames() -> [String: String] {
        let s = prepare("SELECT id, name FROM devices", [])
        defer { sqlite3_finalize(s) }
        var out: [String: String] = [:]
        while sqlite3_step(s) == SQLITE_ROW {
            out[String(cString: sqlite3_column_text(s, 0))] = String(cString: sqlite3_column_text(s, 1))
        }
        return out
    }

    /// Drops everything older than the 7-day retention window.
    func prune() {
        let cutoff = Date().timeIntervalSince1970 - Self.retention
        removeFiles(select("WHERE created < ?", [cutoff]))
        run("DELETE FROM clips WHERE created < ?", cutoff)
        // A file older than the window is never re-imported, so the reminder can go a day after.
        run("DELETE FROM forgotten WHERE at < ?", cutoff - 86400)
    }
}

/// What gets saved. Off means it isn't captured and existing items are hidden everywhere, MCP included.
enum Prefs {
    struct Category { let key, title, icon: String; let detail: String?; let sql: String }
    static let categories: [Category] = [
        .init(key: "text", title: "Copied text", icon: "text.alignleft", detail: nil, sql: "kind = 'text' AND source <> 'iPhone'"),
        .init(key: "images", title: "Copied images", icon: "photo", detail: nil, sql: "kind = 'image' AND source <> 'iPhone'"),
        .init(key: "files", title: "Copied files", icon: "doc", detail: "Files you copy in Finder",
              sql: "kind = 'file' AND source NOT IN ('Screenshot', 'Downloads', 'iPhone')"),
        .init(key: "screenshots", title: "Screenshots", icon: "camera.viewfinder", detail: "Every new screenshot, even ones you don't copy",
              sql: "source = 'Screenshot'"),
        .init(key: "downloads", title: "Recent downloads", icon: "arrow.down.circle", detail: "New files in your Downloads folder",
              sql: "source = 'Downloads'"),
        .init(key: "iphone", title: "Copies from iPhone", icon: "iphone", detail: "Through Universal Clipboard", sql: "source = 'iPhone'"),
    ]
    static func on(_ key: String) -> Bool { UserDefaults.standard.object(forKey: "save." + key) as? Bool ?? true }
    static func set(_ key: String, _ value: Bool) { UserDefaults.standard.set(value, forKey: "save." + key) }
    static var hiddenSQL: String { categories.filter { !on($0.key) }.map { " AND NOT (\($0.sql))" }.joined() }
}

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
