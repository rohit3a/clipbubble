using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClipBubble;

enum ClipKind { Text, Image, File }

/// One clipboard entry. For files, `Text` holds newline-separated paths.
sealed record Clip(long Id, ClipKind Kind, string Text, string? File, string Source, DateTime Created)
{
    public string Hash { get; init; } = "";
    /// When it was copied, exactly as stored (Unix seconds): sync compares these, so they never go through DateTime.
    public double Time { get; init; }
    /// The device it was copied on, for synced items; empty when copied here.
    public string Origin { get; init; } = "";
    /// Synced, but its image or files haven't come over yet.
    public bool Remote { get; init; }
    public string[] Paths => Kind == ClipKind.File ? Text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    /// Null for a synced item whose bytes haven't arrived: its `Text` then holds file names, not paths.
    public string? ImagePath => Remote ? null : Kind == ClipKind.Image ? File : Paths.FirstOrDefault(Store.IsImagePath);
}

static partial class Store
{
    public static readonly string[] ImageExts = { "png", "jpg", "jpeg", "heic", "tiff", "tif", "gif", "webp", "bmp" };
    public static bool IsImagePath(string p) => ImageExts.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant());

    public static string Sha(byte[] d)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(d).Select(b => b.ToString("x2")));
    }
    public static string Sha(string s) => Sha(Encoding.UTF8.GetBytes(s));

    public static string Name(this ClipKind k) => k.ToString().ToLowerInvariant();
    public static ClipKind? ParseKind(string? s) => s switch { "text" => ClipKind.Text, "image" => ClipKind.Image, "file" => ClipKind.File, _ => null };

    /// Writes through a temp file so a reader never sees half a file. Retries briefly if another process
    /// (the MCP server, or a second write) has the file open at that instant.
    public static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        System.IO.File.WriteAllBytes(tmp, data);
        for (var i = 0; ; i++)
        {
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
                else System.IO.File.Move(tmp, path);
                return;
            }
            catch (IOException) when (i < 10) { Thread.Sleep(20); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { System.IO.File.Delete(tmp); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }   // the original error is the one to report
                throw;
            }
        }
    }

    /// Reads without blocking a writer from replacing the file at the same moment.
    public static string ReadText(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// What reading a damaged, unsupported or unreadable picture throws. WIC errors that .NET doesn't map
    /// (a broken WebP, for one) arrive as COMException.
    public static bool Unreadable(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException or COMException or OutOfMemoryException;

    public static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

    /// Waits (up to 2 s) while another app still has the file open for writing, like a screenshot tool saving it.
    /// A half-written picture never matches its copy.
    public static void WaitUntilWritten(string path)
    {
        for (var i = 0; i < 20; i++)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return; }
            catch (IOException) when (System.IO.File.Exists(path)) { Thread.Sleep(100); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        }
    }

    public static Stream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

}

/// Unexpected errors go to errors.log in the data folder instead of vanishing (or closing the app).
static class Log
{
    public static void Error(Exception e)
    {
        try
        {
            var path = Path.Combine(Paths.Dir, "errors.log");
            if (System.IO.File.Exists(path) && new FileInfo(path).Length > 1_000_000) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}");
        }
        catch (Exception) { }   // logging must never be the thing that fails
    }
}

static class Paths
{
    /// `CLIPBUBBLE_HOME` moves everything (tests use it; it also makes a portable copy possible).
    public static readonly string Dir = Environment.GetEnvironmentVariable("CLIPBUBBLE_HOME") is { Length: > 0 } home
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBubble");
    public static readonly string Images = Path.Combine(Dir, "images");
    public static readonly string Db = Path.Combine(Dir, "clips.sqlite");
}

/// SQLite store shared by the app (writer) and the MCP server (reader). Uses the SQLite that ships with Windows.
sealed partial class Db : IDisposable
{
    public static readonly double Retention = 7 * 24 * 3600;
    readonly IntPtr h;

    public Db()
    {
        Directory.CreateDirectory(Paths.Images);
        if (sqlite3_open16(Paths.Db, out h) != 0) throw new IOException("Can't open " + Paths.Db);
        sqlite3_busy_timeout(h, 3000);
        var rc = sqlite3_exec(h, Encoding.UTF8.GetBytes(@"
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS clips(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
              file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS clips_created ON clips(created);
            CREATE TABLE IF NOT EXISTS forgotten(hash TEXT PRIMARY KEY, at REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, os TEXT NOT NULL DEFAULT '');" + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (rc != 0) throw new IOException($"Can't set up {Paths.Db}: " + Marshal.PtrToStringUni(sqlite3_errmsg16(h)));
        // The same columns as the Mac's: where a synced item came from, and whether its bytes are still there.
        if (Scalar("PRAGMA user_version") < 1)
            rc = sqlite3_exec(h, Encoding.UTF8.GetBytes(@"
                BEGIN;
                ALTER TABLE clips ADD COLUMN origin TEXT NOT NULL DEFAULT '';
                ALTER TABLE clips ADD COLUMN remote INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 1;
                COMMIT;" + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (rc != 0) throw new IOException($"Can't update {Paths.Db}: " + Marshal.PtrToStringUni(sqlite3_errmsg16(h)));
    }

    /// Called with the hashes of every insert, bump or delete, from any Db in this process. Sync sets it.
    public static Action<IReadOnlyList<string>>? Changed;
    static void Notify(IReadOnlyList<string> hashes) { if (hashes.Count > 0) Changed?.Invoke(hashes); }

    public void Dispose() => sqlite3_close_v2(h);

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    IntPtr Prepare(string sql, object?[] args)
    {
        if (sqlite3_prepare16_v2(h, sql, -1, out var s, IntPtr.Zero) != 0) throw new InvalidOperationException(Marshal.PtrToStringUni(sqlite3_errmsg16(h)) + "\n" + sql);
        for (var i = 0; i < args.Length; i++)
        {
            var idx = i + 1;
            switch (args[i])
            {
                case string v: sqlite3_bind_text16(s, idx, v, v.Length * 2, Transient); break;   // explicit length keeps text after a NUL
                case double v: sqlite3_bind_double(s, idx, v); break;
                case long v: sqlite3_bind_int64(s, idx, v); break;
                case int v: sqlite3_bind_int64(s, idx, v); break;
                default: sqlite3_bind_null(s, idx); break;
            }
        }
        return s;
    }

    /// Throws when SQLite reports an error (a database still locked after the 3 s busy timeout, a full disk),
    /// so a failed write is never mistaken for a saved clip.
    void Run(string sql, params object?[] args)
    {
        var s = Prepare(sql, args);
        try { Check(sqlite3_step(s)); }
        finally { sqlite3_finalize(s); }
    }

    void Check(int rc)
    {
        if (rc != SQLITE_ROW && rc != SQLITE_DONE) throw new InvalidOperationException("SQLite: " + Marshal.PtrToStringUni(sqlite3_errmsg16(h)));
    }

    List<Clip> Select(string tail, params object?[] args)
    {
        var s = Prepare("SELECT id,kind,text,file,source,created,hash,origin,remote FROM clips " + tail, args);
        try
        {
            string Col(int i) => sqlite3_column_text16(s, i) is var p && p != IntPtr.Zero ? Marshal.PtrToStringUni(p, sqlite3_column_bytes16(s, i) / 2) : "";
            var output = new List<Clip>();
            int rc;
            while ((rc = sqlite3_step(s)) == SQLITE_ROW)
            {
                if (Store.ParseKind(Col(1)) is not { } kind) continue;
                var time = sqlite3_column_double(s, 5);
                output.Add(new Clip(sqlite3_column_int64(s, 0), kind, Col(2),
                    sqlite3_column_type(s, 3) == SQLITE_NULL ? null : Col(3), Col(4),
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(time * 1000)).LocalDateTime)
                    { Time = time, Hash = Col(6), Origin = Col(7), Remote = sqlite3_column_int64(s, 8) != 0 });
            }
            Check(rc);
            return output;
        }
        finally { sqlite3_finalize(s); }
    }

    /// Inserts a clip, or moves an identical existing one to the top. `keepSource` is for ClipBubble's own writes
    /// (a picked clip, an agent's clipboard_set): a known clip keeps its source, a new one gets `source`.
    public void Upsert(ClipKind kind, string text, string? file, string source, string hash, bool keepSource = false)
    {
        Run(@"INSERT INTO clips(kind,text,file,source,hash,created) VALUES(?,?,?,?,?,?)
              ON CONFLICT(hash) DO UPDATE SET created=excluded.created,
                source=CASE WHEN clips.source IN ('Screenshot', 'Downloads') OR ?7 THEN clips.source ELSE excluded.source END",
            kind.Name(), text, file, source, hash, Now, keepSource ? 1 : 0);
        Notify(new[] { hash });
    }

    /// Adds a clip with its own timestamp, leaving any existing identical clip untouched, unless it was deleted
    /// (here or on a linked device) after that time.
    public void InsertIfMissing(ClipKind kind, string text, string source, string hash, DateTime created)
    {
        var at = new DateTimeOffset(created).ToUnixTimeMilliseconds() / 1000.0;
        Run(@"INSERT OR IGNORE INTO clips(kind,text,source,hash,created)
              SELECT ?,?,?,?,? WHERE NOT EXISTS (SELECT 1 FROM forgotten WHERE hash = ?4 AND at >= ?5)",
            kind.Name(), text, source, hash, at);
        if (sqlite3_changes(h) > 0) Notify(new[] { hash });
    }

    public List<Clip> Clips(int limit = 300, ClipKind? kind = null, string? source = null, string? query = null, double? sinceHours = null)
    {
        var tail = "WHERE created >= ?";
        var args = new List<object?> { Now - (sinceHours.HasValue ? sinceHours.Value * 3600 : Retention) };
        if (kind == ClipKind.Image)
        {
            // Image files (like screenshots) count as images too.
            tail += " AND (kind = 'image' OR (kind = 'file' AND (" + string.Join(" OR ", Store.ImageExts.Select(e => $"text LIKE '%.{e}'")) + ")))";
        }
        else if (kind is { } k)
        {
            tail += " AND kind = ?";
            args.Add(k.Name());
        }
        // A linked device's name finds everything synced from it.
        if (source != null) { tail += " AND (source = ? COLLATE NOCASE OR origin IN (SELECT id FROM devices WHERE name = ? COLLATE NOCASE))"; args.Add(source); args.Add(source); }
        if (!string.IsNullOrEmpty(query)) { tail += " AND (text LIKE ? OR source LIKE ?)"; args.Add($"%{query}%"); args.Add($"%{query}%"); }
        tail += Prefs.HiddenSql;
        tail += " ORDER BY created DESC LIMIT ?";
        args.Add(limit);
        return Select(tail, args.ToArray());
    }

    /// Switched-off categories stay hidden here too, so an agent can't fetch them by id.
    public Clip? Clip(long id) => Select("WHERE id = ?" + Prefs.HiddenSql, id).FirstOrDefault();

    public bool Exists(string hash) => Select("WHERE hash = ?", hash).Count > 0;

    /// Any clip by hash, switched-off categories included: sync needs to know what's stored.
    public Clip? ByHash(string hash) => Select("WHERE hash = ?", hash).FirstOrDefault();

    public void Delete(string hash) => DeleteWhere("WHERE hash = ?", hash);
    public void Delete(long id) => DeleteWhere("WHERE id = ?", id);
    public void DeleteAll() => DeleteWhere("");

    /// Drops everything older than the 7-day retention window, and deletes no device needs to hear about any more.
    public void Prune()
    {
        DeleteWhere("WHERE created < ?", forget: false, Now - Retention);
        Run("DELETE FROM forgotten WHERE at < ?", Now - Retention - 86400);
    }

    void DeleteWhere(string where, params object?[] args) => DeleteWhere(where, forget: true, args);

    /// Rows go first and files after, so a delete that fails (a locked database) never leaves a row without its image.
    /// A delete is recorded with its time, so folders don't re-import it and linked devices hear about it.
    void DeleteWhere(string where, bool forget, params object?[] args)
    {
        var gone = Select(where, args);
        if (forget && gone.Count > 0)
            Run("INSERT OR REPLACE INTO forgotten(hash, at) SELECT hash, ? FROM clips " + where, new object?[] { Now }.Concat(args).ToArray());
        Run("DELETE FROM clips " + where, args);
        RemoveFiles(gone);
        if (forget) Notify(gone.Select(c => c.Hash).ToList());
    }

    // MARK: Sync

    public long Scalar(string sql, params object?[] args)
    {
        var s = Prepare(sql, args);
        try { return sqlite3_step(s) == SQLITE_ROW ? sqlite3_column_int64(s, 0) : 0; }
        finally { sqlite3_finalize(s); }
    }

    /// Moves whenever another connection commits, so the engine notices agents' writes from the MCP process.
    public long DataVersion() => Scalar("PRAGMA data_version");

    List<Stamp> StampRows(string sql, params object?[] args)
    {
        var s = Prepare(sql, args);
        try
        {
            var output = new List<Stamp>();
            while (sqlite3_step(s) == SQLITE_ROW)
                output.Add(new Stamp(Marshal.PtrToStringUni(sqlite3_column_text16(s, 0), sqlite3_column_bytes16(s, 0) / 2), sqlite3_column_double(s, 1), sqlite3_column_int64(s, 2) == 1));
            return output;
        }
        finally { sqlite3_finalize(s); }
    }

    /// Every item and delete of the last 7 days, the newest of each. `includingWaiting: false` leaves out synced
    /// items whose bytes haven't arrived, so peers offer them again.
    public List<Stamp> Stamps(bool includingWaiting = true)
    {
        var cutoff = Now - Retention;
        var best = new Dictionary<string, Stamp>();
        foreach (var st in StampRows($@"SELECT hash, created, 0 FROM clips WHERE created >= ?{(includingWaiting ? "" : " AND remote = 0")}{Prefs.HiddenSql}
                                       UNION ALL SELECT hash, at, 1 FROM forgotten WHERE at >= ?", cutoff, cutoff))
            if (st.Beats(best.TryGetValue(st.Hash, out var b) ? b : null)) best[st.Hash] = st;
        return best.Values.ToList();
    }

    public Stamp? Stamp(string hash)
    {
        Stamp? best = null;
        foreach (var st in StampRows("SELECT hash, created, 0 FROM clips WHERE hash = ? UNION ALL SELECT hash, at, 1 FROM forgotten WHERE hash = ?", hash, hash))
            if (st.Beats(best)) best = st;
        return best;
    }

    /// Whether a category that's switched off would hide it.
    public bool Allowed(string kind, string source) =>
        Scalar("SELECT 1 FROM (SELECT ? AS kind, ? AS source) WHERE 1" + Prefs.HiddenSql, kind, source) == 1;

    /// A synced item. Files and images arrive later, so it starts as `remote` with just the names.
    public void Insert(Entry e, string origin)
    {
        Run(@"INSERT INTO clips(kind,text,source,hash,created,origin,remote) VALUES(?,?,?,?,?,?,?)
              ON CONFLICT(hash) DO UPDATE SET created = max(created, excluded.created)",
            e.Kind, e.Kind == "image" ? "" : e.Text, e.Source, e.Hash, e.Time, origin, e.Blobs.Count > 0 ? 1 : 0);
        Notify(new[] { e.Hash });
    }

    public void Bump(string hash, double to)
    {
        Run("UPDATE clips SET created = ? WHERE hash = ? AND created < ?", to, hash, to);
        Notify(new[] { hash });
    }

    /// A delete from another device. Only removes what was copied before it.
    public void Tombstone(Stamp st)
    {
        var gone = Select("WHERE hash = ? AND created <= ?", st.Hash, st.Time);
        Run("DELETE FROM clips WHERE hash = ? AND created <= ?", st.Hash, st.Time);
        Run("INSERT INTO forgotten(hash, at) VALUES(?, ?) ON CONFLICT(hash) DO UPDATE SET at = max(at, excluded.at)", st.Hash, st.Time);
        RemoveFiles(gone);
        Notify(new[] { st.Hash });
    }

    /// A synced item's bytes arrived: an image's file, or the files' new paths.
    public void Received(string hash, string? file = null, string? text = null)
    {
        if (text != null) Run("UPDATE clips SET remote = 0, text = ? WHERE hash = ?", text, hash);
        else Run("UPDATE clips SET remote = 0, file = ? WHERE hash = ?", file, hash);
    }

    public void SaveDevice(string id, string name, string os) =>
        Run("INSERT INTO devices(id,name,os) VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name, os=excluded.os", id, name, os);

    public Dictionary<string, string> DeviceNames()
    {
        var s = Prepare("SELECT id, name FROM devices", Array.Empty<object?>());
        try
        {
            var output = new Dictionary<string, string>();
            while (sqlite3_step(s) == SQLITE_ROW)
                output[Marshal.PtrToStringUni(sqlite3_column_text16(s, 0)) ?? ""] = Marshal.PtrToStringUni(sqlite3_column_text16(s, 1)) ?? "";
            return output;
        }
        finally { sqlite3_finalize(s); }
    }

    /// Only our own copies of images are deleted, never a screenshot or download the clip points at.
    static void RemoveFiles(IEnumerable<Clip> clips)
    {
        foreach (var c in clips)
            if (c.File is { } f && Path.GetDirectoryName(f) == Paths.Images) try { System.IO.File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    const int SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_NULL = 5;
    static readonly IntPtr Transient = new(-1);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_open16(string path, out IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, IntPtr err);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_bind_text16(IntPtr s, int i, string v, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_double(IntPtr s, int i, double v);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_int64(IntPtr s, int i, long v);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_null(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern double sqlite3_column_double(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text16(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_type(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes16(IntPtr s, int i);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_errmsg16(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_changes(IntPtr db);
}

/// What gets saved, plus the app's other settings (the Mac keeps these in UserDefaults).
/// Off means a category isn't captured and existing items are hidden everywhere, MCP included.
static class Prefs
{
    public sealed record Category(string Key, string Title, string Icon, string? Detail, string Sql);

    /// Windows has no Universal Clipboard, so the Mac's iPhone category has no switch here.
    public static readonly Category[] Categories =
    {
        new("text", "Copied text", "\uE8E4", null, "kind = 'text' AND source <> 'iPhone'"),
        new("images", "Copied images", "\uEB9F", null, "kind = 'image' AND source <> 'iPhone'"),
        new("files", "Copied files", "\uE8A5", "Files you copy in File Explorer", "kind = 'file' AND source NOT IN ('Screenshot', 'Downloads', 'iPhone')"),
        new("screenshots", "Screenshots", "\uE722", "Every new screenshot, even ones you don't copy", "source = 'Screenshot'"),
        new("downloads", "Recent downloads", "\uE896", "New files in your Downloads folder", "source = 'Downloads'"),
    };

    static readonly string File = Path.Combine(Paths.Dir, "prefs.json");
    static volatile Dictionary<string, object?> values = Load() ?? new();   // swapped whole; read from the writer thread too

    /// Null when the file can't be read right now; a missing file means defaults.
    static Dictionary<string, object?>? Load()
    {
        if (!System.IO.File.Exists(File)) return new();
        try { return Store.Json.Deserialize<Dictionary<string, object?>>(Store.ReadText(File)) ?? new(); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    /// The MCP server calls this before each query so it sees switches flipped in the running app. If the file
    /// can't be read, the last known switches stay, so a hidden category never briefly reappears.
    public static void Reload() => values = Load() ?? values;

    public static T Get<T>(string key, T fallback) => values.TryGetValue(key, out var v) && v is T t ? t : fallback;

    /// Copy, save, then swap: the capture thread may be reading `values`, and memory only changes once the file has.
    public static void Set(string key, object? value)
    {
        var next = new Dictionary<string, object?>(values);
        if (value == null) next.Remove(key); else next[key] = value;
        Directory.CreateDirectory(Paths.Dir);
        Store.WriteAtomic(File, Encoding.UTF8.GetBytes(Store.Json.Serialize(next)));
        values = next;
    }

    public static bool On(string key) => Get("save." + key, true);
    public static void SetOn(string key, bool on) => Set("save." + key, on);
    public static string HiddenSql => string.Concat(Categories.Where(c => !On(c.Key)).Select(c => $" AND NOT ({c.Sql})"));
}

/// Screenshots and downloads the user took out of history. Their files stay in the folder, so without this the
/// backfill would add them right back. Only touched on the writer thread.
sealed class Forgotten
{
    static readonly string File = Path.Combine(Paths.Dir, "forgotten.json");

    /// Unix milliseconds of the last Clear all: files added before it stay out.
    public long ClearedAt { get; set; }
    /// Single files removed since, with when (dropped after 7 days, when the backfill no longer reaches them).
    /// A new file saved at the same path later still comes in.
    public Dictionary<string, long> Files { get; set; } = new();

    /// Null when the file is there but can't be read right now: the backfill then waits rather than bringing
    /// everything back, and a removal fails rather than rewriting the list without the others.
    public static Forgotten? Load()
    {
        try { return System.IO.File.Exists(File) ? Store.Json.Deserialize<Forgotten>(Store.ReadText(File)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return new(); }   // not JSON: start over
    }

    public bool Has(string path, DateTime added)
    {
        var at = new DateTimeOffset(added).ToUnixTimeMilliseconds();
        return at <= ClearedAt || Files.TryGetValue(path, out var removed) && at <= removed;
    }

    public static void Remove(Clip c)
    {
        if (c.Source is "Screenshot" or "Downloads") Save(f => f.Files[c.Text] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static void ClearAll() => Save(f => { f.ClearedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); f.Files.Clear(); });

    static void Save(Action<Forgotten> change)
    {
        var f = Load() ?? throw new IOException("Can't read " + File);
        change(f);
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(Db.Retention * 1000);
        f.Files = f.Files.Where(p => p.Value >= cutoff).ToDictionary(p => p.Key, p => p.Value);
        Store.WriteAtomic(File, Encoding.UTF8.GetBytes(Store.Json.Serialize(f)));
    }
}
