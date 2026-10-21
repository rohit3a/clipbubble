using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClipBubble;

/// What was on the clipboard, copied out while it was open. Decoding and hashing happen later, off the UI thread.
sealed record Snapshot(string[] Files, string? Text, byte[]? Png, byte[]? Dib);

/// Everything that writes history (copies, new screenshots and downloads, the 7-day backfill, deletes and cleanup)
/// runs in order on this one background queue, so the UI never waits on the database or the disk, and a screenshot's
/// copy and its saved file are always handled one after the other.
sealed class Writer
{
    /// Its own connection: the UI's reads don't wait for a write in progress.
    public readonly Db Db = new();
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    Task chain = Task.CompletedTask;

    /// Runs `work` after everything queued before it, then `done` on the UI thread if `work` returned true.
    public void Run(Func<bool> work, Action done)
    {
        chain = chain.ContinueWith(_ =>
        {
            try { if (work()) ui.BeginInvoke(done); }
            catch (Exception e) { Log.Error(e); }   // e.g. the database stayed locked: this item is lost, the next one isn't
        }, TaskScheduler.Default);
    }

    /// Waits for everything queued so far, up to `timeout`.
    public bool Finish(TimeSpan timeout) => chain.Wait(timeout);

    /// A delete or cleanup. `then` runs on the UI thread even if it failed (and gets the error), so a view that
    /// re-reads shows what's really stored.
    public void Write(Action<Db> write, Action<Exception?>? then = null)
    {
        Exception? failed = null;
        Run(() =>
        {
            try { write(Db); }
            catch (Exception e) { Log.Error(e); failed = e; }
            return true;
        }, () => then?.Invoke(failed));
    }
}

/// Listens for clipboard changes and records each one, on the writer, once it has settled.
sealed class Capture : IDisposable
{
    readonly HwndSource listener;
    readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromSeconds(0.6) };
    readonly Writer writer;
    readonly Recorder recorder;
    readonly Action onChange;
    uint seen, handled;
    (string Name, bool Ours) source = ("", false);
    int busyRetries;

    public Capture(Writer writer, Action onChange)
    {
        this.writer = writer;
        recorder = new Recorder(writer.Db);
        this.onChange = onChange;
        handled = seen = GetClipboardSequenceNumber();
        listener = new HwndSource(new HwndSourceParameters("ClipBubble clipboard") { ParentWindow = new IntPtr(-3) });   // message-only
        listener.AddHook(Hook);
        AddClipboardFormatListener(listener.Handle);
        settle.Tick += (_, _) => Settled();
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(listener.Handle);
        listener.Dispose();
        settle.Stop();
    }

    // Let the clipboard settle first. Paste-through tools like Wispr Flow put text on it, press Ctrl+V and then
    // restore the old contents; reading during that window could make them paste the wrong thing.
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handledMsg)
    {
        if (msg != 0x031D) return IntPtr.Zero;   // WM_CLIPBOARDUPDATE
        seen = GetClipboardSequenceNumber();
        source = Apps.ClipboardSource();   // who copied it; by the time it settles another app may be in front
        busyRetries = 0;
        settle.Stop();
        settle.Start();
        return IntPtr.Zero;
    }

    void Settled()
    {
        settle.Stop();
        if (seen == handled) return;
        IDataObject? data;
        try { data = Clipboard.GetDataObject(); }
        catch (COMException) when (busyRetries++ < 2) { settle.Start(); return; }   // another app has it open (each try waits ~1 s)
        catch (COMException) { handled = seen; return; }
        handled = seen;
        if (data == null || Read(data) is not { } snapshot) return;
        var (from, at) = (source, DateTime.Now);   // paste-through tools are spotted by when things were copied, not by when the writer got to them
        writer.Run(() => recorder.Record(snapshot, from.Name, at, from.Ours), onChange);
    }

    /// Adds files from a watched folder (`files` is listed on the writer too, so a big backfill never blocks the UI).
    public void AddFiles(string source, Func<IEnumerable<(string Path, DateTime Added)>> files, Action done) =>
        writer.Run(() => { foreach (var (path, added) in files()) recorder.AddFile(path, source, added); return true; }, done);

    /// Copies the formats we care about. Copies marked as secret by password managers are never read.
    internal static Snapshot? Read(IDataObject data)
    {
        string[] formats;
        try { formats = data.GetFormats(false); }
        catch (COMException) { return null; }
        if (formats.Length == 0 || formats.Contains(Board.ExcludeFormat) || formats.Contains("Clipboard Viewer Ignore")) return null;
        if (formats.Contains("CanIncludeInClipboardHistory") && Bytes(data, "CanIncludeInClipboardHistory") is { Length: >= 4 } allow && BitConverter.ToInt32(allow, 0) == 0) return null;
        var files = formats.Contains(DataFormats.FileDrop) ? Get(data, DataFormats.FileDrop) as string[] : null;
        // Like the DIB below: apps that only put ANSI text on get a Unicode copy made by Windows on request.
        var text = formats.Contains(DataFormats.UnicodeText) || formats.Contains(DataFormats.Text) || formats.Contains(DataFormats.OemText)
            ? Get(data, DataFormats.UnicodeText) as string : null;
        var png = formats.Contains("PNG") ? Bytes(data, "PNG") : null;
        // Asked for even when unlisted: apps that copy only a Bitmap (all .NET apps) get a DIB made by Windows on request.
        var dib = png == null && (formats.Contains(DataFormats.Dib) || formats.Contains(DataFormats.Bitmap)) ? Bytes(data, DataFormats.Dib) : null;
        return new Snapshot(files ?? Array.Empty<string>(), text, png, dib);
    }

    static object? Get(IDataObject data, string format)
    {
        try { return data.GetData(format); }
        catch (Exception e) when (e is COMException or OutOfMemoryException or InvalidOperationException) { return null; }
    }

    static byte[]? Bytes(IDataObject data, string format) => Get(data, format) is MemoryStream ms ? ms.ToArray() : null;

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

/// Turns settled clipboard snapshots into history rows, and spots tools that paste through the clipboard.
/// Runs on one background thread at a time.
sealed class Recorder(Db db)
{
    /// Copies ClipBubble is about to make, by the hash capture will see: null leaves history alone (a copy synced from
    /// another machine is already there), a hash moves that item up instead (a synced file, now at a path here).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? MoveUp, DateTime At)> expected = new();

    public static void Expect(string captured, string? moveUp) => expected[captured] = (moveUp, DateTime.UtcNow);

    /// The hash capture gives a clip once Board.Write has put it on the clipboard.
    public static string CaptureHash(Clip c) =>
        c.Kind == ClipKind.File ? "f:" + Store.Sha(string.Join("\n", c.Paths.Where(p => File.Exists(p) || Directory.Exists(p)))) : c.Hash;

    /// The latest capture and the one before it, to spot tools that paste through the clipboard.
    (string Hash, DateTime At, bool Inserted)? last;
    string? previousHash;

    /// A file found in a watched folder. A new screenshot replaces the copy of the same picture made moments ago.
    public void AddFile(string path, string source, DateTime added)
    {
        db.InsertIfMissing(ClipKind.File, path, source, "f:" + Store.Sha(path), added);
        if (source == "Screenshot") db.DropCopiesOf(path);
    }

    /// Returns whether anything changed that the UI should show.
    /// `ours` means ClipBubble itself put it on the clipboard (see Db.Upsert's keepSource).
    public bool Record(Snapshot s, string source, DateTime now, bool ours = false)
    {
        if (Classify(s) is not { } clip) return false;
        if (ours && expected.TryRemove(clip.Hash, out var e) && (DateTime.UtcNow - e.At).TotalSeconds < 5 && e.MoveUp != clip.Hash)
        {
            if (e.MoveUp == null) return false;
            db.Bump(e.MoveUp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
            return true;
        }
        if (clip.Hash == last?.Hash && db.Exists(clip.Hash)) return false;   // copied again after a delete: save it again
        var category = clip.Kind switch { ClipKind.Image => "images", ClipKind.File => "files", _ => "text" };
        if (!Prefs.On(category)) return false;

        // The previous contents came straight back, so what we just saw was a paste-through: drop it,
        // and don't bump the restored item.
        if (clip.Hash == previousHash && last is { } l && (now - l.At).TotalSeconds < 2.5 && db.Exists(clip.Hash))
        {
            if (l.Inserted) db.Delete(l.Hash);
            last = (clip.Hash, now, false);
            previousHash = null;
            return true;
        }

        var inserted = !db.Exists(clip.Hash);
        string? file = null;
        if (clip.Png is { } png)
        {
            file = Path.Combine(Paths.Images, clip.Hash.Substring(2) + ".png");
            if (!File.Exists(file)) Store.WriteAtomic(file, png);
        }
        db.Upsert(clip.Kind, clip.Text, file, source, clip.Hash, keepSource: ours);
        // Checked after writing, like DropCopiesOf on the screenshot side, so whichever of the two lands second
        // removes the copy, even when they arrive at the same moment.
        if (clip.Png is { } shot && db.IsRecentScreenshot(shot))
        {
            db.Delete(clip.Hash);
            return false;
        }
        previousHash = last?.Hash;
        last = (clip.Hash, now, inserted);
        return true;
    }

    record Classified(ClipKind Kind, string Text, string Hash, byte[]? Png);

    /// Text bigger than this (a log dump, a database export) isn't kept: it would slow every list and search.
    public const int MaxTextBytes = 2_000_000;

    /// Same order as the Mac: files, then an image unless there's real text alongside it, then text.
    static Classified? Classify(Snapshot s)
    {
        var trimmed = s.Text?.Trim() ?? "";
        var looksLikeUrl = !trimmed.Contains(' ') && !trimmed.Contains('\n') && trimmed.StartsWith("http");
        if (s.Files.Length > 0)
        {
            var paths = string.Join("\n", s.Files);
            return new(ClipKind.File, paths, "f:" + Store.Sha(paths), null);
        }
        if ((trimmed.Length == 0 || looksLikeUrl) && (s.Png ?? DibToPng(s.Dib)) is { } png)
            return new(ClipKind.Image, "", "i:" + Store.Sha(png), png);
        if (trimmed.Length > 0 && Encoding.UTF8.GetByteCount(s.Text!) <= MaxTextBytes) return new(ClipKind.Text, s.Text!, "t:" + Store.Sha(s.Text!), null);
        return null;
    }

    /// A clipboard DIB is a .bmp file without its 14-byte file header; add one and let WIC decode it.
    static byte[]? DibToPng(byte[]? dib)
    {
        if (dib == null || dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), used = BitConverter.ToInt32(dib, 32);
        var masks = headerSize == 40 && compression == 3 ? 12 : 0;
        var palette = bits <= 8 ? (used != 0 ? used : 1 << bits) * 4 : 0;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(14 + headerSize + masks + palette).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        try
        {
            return Store.Png(BitmapDecoder.Create(new MemoryStream(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]);
        }
        catch (Exception e) when (Store.Unreadable(e)) { return null; }
    }
}

/// Names and icons of the apps things were copied from.
static class Apps
{
    static readonly string OwnExe = Process.GetCurrentProcess().MainModule!.FileName;
    static readonly string File = Path.Combine(Paths.Dir, "apps.json");
    /// Remembered across launches so rows keep their app icons after a restart.
    static readonly Dictionary<string, string> PathsByName = Load();

    static Dictionary<string, string> Load()
    {
        try { return Store.Json.Deserialize<Dictionary<string, string>>(Store.ReadText(File)) ?? new(); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return new(); }
    }

    /// The app that put what's on the clipboard there. When that's ClipBubble itself (a picked clip, or its MCP
    /// server for an agent), it's the app in front instead, like the Mac, and `Ours` is set.
    public static (string Name, bool Ours) ClipboardSource()
    {
        var owner = GetClipboardOwner();
        var exe = ExeOf(owner != IntPtr.Zero ? owner : Win.GetForegroundWindow());
        if (!IsOurs(exe)) return (Name(exe) ?? "Windows", false);
        var front = ExeOf(Win.GetForegroundWindow());
        return (IsOurs(front) ? "Windows" : Name(front) ?? "Windows", true);
    }

    static string? ExeOf(IntPtr hwnd)
    {
        Win.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : ExePath((int)pid);
    }

    static bool IsOurs(string? exe) => exe != null && string.Equals(exe, OwnExe, StringComparison.OrdinalIgnoreCase);

    static string? Name(string? exe)
    {
        if (exe == null) return null;
        string name;
        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim() ?? ""; }
        catch (System.IO.FileNotFoundException) { name = ""; }
        name = Readable(name.Length == 0 ? Path.GetFileName(exe) : name);
        lock (PathsByName)
        {
            if (PathsByName.TryGetValue(name, out var known) && known == exe) return name;
            PathsByName[name] = exe;
            try { Store.WriteAtomic(File, Encoding.UTF8.GetBytes(Store.Json.Serialize(PathsByName))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return name;
    }

    /// Some apps (Snipping Tool, many Store apps) give their file name as their description:
    /// "SnippingTool.exe" reads as "Snipping Tool", "Microsoft.Photos.exe" as "Photos".
    internal static string Readable(string name)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return name;
        var core = name.Substring(0, name.Length - 4);
        core = core.Substring(core.LastIndexOf('.') + 1);
        return System.Text.RegularExpressions.Regex.Replace(core, "(?<=[a-z])(?=[A-Z])", " ");
    }

    /// The exe behind a source name seen this session, so rows can show its icon.
    public static string? ExePath(string name) { lock (PathsByName) return PathsByName.TryGetValue(name, out var p) ? p : null; }

    static string? ExePath(int pid)
    {
        var h = Win.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Win.CloseHandle(h); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}

/// Watches a folder (screenshots, Downloads) and hands each new item that passes `include` to `onNew`.
sealed class FolderWatcher : IDisposable
{
    public static readonly HashSet<string> TempExts = new() { "crdownload", "download", "part", "partial", "tmp", "opdownload" };

    /// Where Windows saves screenshots (Win+PrtScn and Snipping Tool's auto-save): Pictures\Screenshots.
    public static string DefaultScreenshotFolder => KnownFolder(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"))
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// A folder picked in Settings wins over the default.
    public static string ScreenshotFolder => Prefs.Get<string?>("screenshotFolder", null) is { Length: > 0 } custom ? custom : DefaultScreenshotFolder;

    public static string? DownloadsFolder => KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"));

    public static bool CanRead(string? folder)
    {
        if (folder == null) return false;
        try { Directory.EnumerateFileSystemEntries(folder).Any(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public readonly string Folder;
    readonly Func<string, bool> include;
    readonly double? maxAge;
    readonly Action<string, DateTime> onNew;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromSeconds(0.4) };
    /// Every 5 s: starts watching once the folder exists, and again if it was renamed or replaced, because Windows
    /// keeps watching a renamed folder under its new name and says nothing.
    readonly DispatcherTimer check = new() { Interval = TimeSpan.FromSeconds(5) };
    (uint Volume, ulong Index)? watchedId;
    /// Null until the folder could be listed once. Until then only files created after `since` count as new,
    /// so a folder that appears later (Snipping Tool's first save) reports its new screenshot, while one that was
    /// missing or unreadable for a while (an unplugged drive) doesn't report everything already in it.
    HashSet<string>? known;
    /// Files created after this count as new while `known` is null: when the watcher started, and later the last time
    /// the 5 s check saw the folder fine (so a folder restored days later doesn't announce what was already there).
    DateTime since = DateTime.Now;
    FileSystemWatcher? watcher;
    bool stopped;

    /// `maxAge` ignores files older than that (so moving old screenshots in doesn't count); null takes everything new.
    public FolderWatcher(string folder, double? maxAge, Func<string, bool> include, Action<string, DateTime> onNew)
    {
        Folder = folder;
        this.maxAge = maxAge;
        this.include = include;
        this.onNew = onNew;
        debounce.Tick += (_, _) => { debounce.Stop(); Scan(); };
        check.Tick += (_, _) => Check();
        if (Listing() is { } now) known = new(now);
        Start();
    }

    public void Dispose()
    {
        stopped = true;
        check.Stop();
        debounce.Stop();
        watcher?.Dispose();
        watcher = null;
    }

    /// Waits until the folder exists (Snipping Tool creates Screenshots on the first save). Each (re)start ends with
    /// a scan against what was known before, so files that arrived while the folder was missing or the watcher was
    /// down still count as new.
    void Start()
    {
        if (stopped) return;
        check.Start();
        if (!Directory.Exists(Folder)) return;
        // A different folder than last time (renamed and made again, deleted and restored): its files aren't new,
        // except ones made since it was last seen fine. One that can't be told apart is taken as the same folder.
        var id = Identity(Folder);
        if (watchedId != null && id != null && id != watchedId) known = null;
        if (id != null) watchedId = id;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var w = new FileSystemWatcher(Folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, IncludeSubdirectories = false };
        FileSystemEventHandler changed = (_, _) => dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
        w.Created += changed;
        w.Renamed += (s, e) => changed(s, e);
        w.Error += (_, _) => dispatcher.BeginInvoke(new Action(Restart));   // e.g. the folder was deleted
        try { w.EnableRaisingEvents = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            w.Dispose();   // no access yet: the next check tries again
            return;
        }
        watcher = w;
        Scan();
    }

    bool checking;

    /// Off the UI thread: a folder on a network drive that stopped answering can take seconds to look at.
    void Check()
    {
        if (checking) return;
        checking = true;
        var (dispatcher, watching, id, probed) = (Dispatcher.CurrentDispatcher, watcher != null, watchedId, DateTime.Now);
        Task.Run(() =>
        {
            var exists = Directory.Exists(Folder);
            var now = exists ? Identity(Folder) : null;
            var replaced = now != null && id != null && now != id;
            var restart = watching ? !exists || replaced : exists;
            dispatcher.BeginInvoke(new Action(() =>
            {
                checking = false;
                if (stopped) return;
                if (restart) { Restart(); return; }
                if (watching && exists) since = probed;
                if (watchedId == null && now != null) watchedId = now;   // it became readable
            }));
        });
    }

    void Restart()
    {
        watcher?.Dispose();
        watcher = null;
        Start();
    }

    /// Which folder is at that path, by its file ID. Not its creation date: NTFS hands a folder made within seconds
    /// of renaming the old one the old one's date. Null when it can't be read.
    static (uint, ulong)? Identity(string folder)
    {
        const uint readAttributes = 0x80, shareAll = 7, openExisting = 3, backupSemantics = 0x02000000;   // needed to open a folder
        using var h = CreateFile(folder, readAttributes, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return (info.VolumeSerialNumber, (ulong)info.FileIndexHigh << 32 | info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct HandleInfo { public uint Attributes; public long Created, Accessed, Written; public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out HandleInfo info);

    public static DateTime Added(string path)
    {
        try { return File.GetCreationTime(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// Everything already in the folder from the last `days` days, for backfilling history.
    public IEnumerable<(string Path, DateTime Added)> Existing(double days)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        return (Listing() ?? new()).Select(p => (p, Added(p))).Where(x => x.Item2 > cutoff).ToList();
    }

    /// Null when the folder can't be listed right now (gone, no access, a network share not answering).
    List<string>? Listing()
    {
        try
        {
            return new DirectoryInfo(Folder).EnumerateFileSystemInfos()
                .Where(i => (i.Attributes & FileAttributes.Hidden) == 0)
                .Select(i => i.FullName).Where(include).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    void Scan()
    {
        if (stopped || Listing() is not { } now) return;
        var fresh = now.Where(p => known == null ? Added(p) >= since : !known.Contains(p)).ToList();
        known = new HashSet<string>(now);
        DateTime? cutoff = maxAge.HasValue ? DateTime.Now.AddSeconds(-maxAge.Value) : null;
        foreach (var path in fresh.OrderBy(Added))
        {
            var date = Added(path);
            if (cutoff.HasValue && date < cutoff) continue;
            onNew(path, maxAge == null ? DateTime.Now : date);
        }
    }

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
