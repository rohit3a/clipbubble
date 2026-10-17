using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;

namespace ClipBubble;

// Links this machine's history with ClipBubble elsewhere over SSH (docs/sync-protocol.md), like the Mac and Linux.
// Engine state is touched only on the engine thread, which runs posted actions in order. Each session has a reader
// thread and a writer thread; writes put control frames ahead of file chunks.

static class SyncLog
{
    public static void Write(string line)
    {
        try
        {
            var path = Path.Combine(Paths.Dir, "sync.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
            {
                File.Copy(path, path + ".1", true);
                File.Delete(path);
            }
            File.AppendAllText(path, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ ") + line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

sealed record Transfer(long Done, long Total, string Peer, bool Sending)
{
    public double Fraction => Total > 0 ? Math.Min(1, (double)Done / Total) : 0;
}

/// A host's link state: live (Arg is the peer's name), offline (At is when it retries), or why it can't link.
sealed record LinkState(string Kind, string Arg = "", double At = 0)
{
    public string Text
    {
        get
        {
            switch (Kind)
            {
                case "live": return "Linked with " + Arg;
                case "offline":
                    var s = At > 0 ? Math.Max(0, (int)(At - Engine.Now)) : 0;
                    return s is > 0 and < 60 ? $"Offline, retrying in {s} s" : s > 0 ? $"Offline, retrying in {s / 60} min" : "Offline";
                case "connecting": return "Connecting…";
                case "needs-key": return "Needs SSH key login";
                case "unknown-host": return "Connect once in a terminal to trust this host";
                case "not-installed": return "ClipBubble isn't installed there";
                case "not-running": return "ClipBubble isn't running there";
                case "version": return $"Update needed (it has {Arg})";
                default: return Kind;
            }
        }
    }
}

sealed class Engine
{
    public const string Version = "1.2.0";
    public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// Where files from other devices go: Downloads\ClipBubble\<device>. The app sets the real Downloads folder.
    public static Func<string> Downloads = () => Environment.GetEnvironmentVariable("CLIPBUBBLE_DOWNLOADS") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    readonly BlockingCollection<Action> queue = new();
    public readonly string Device;
    public string Name;
    internal Db db = null!;
    internal readonly List<Session> Sessions = new();
    readonly Dictionary<string, HostLink> links = new();
    readonly Dictionary<string, double> sentAt = new();
    readonly HashSet<string> wanted = new();
    bool ticking;
    Dictionary<string, Transfer> lastTransfers = new();

    /// Puts a synced item on the clipboard. Called on the engine thread.
    public Action<Clip> Apply = _ => { };
    public Action Received = () => { };
    public Action Status = () => { };
    /// Called on the engine thread with every transfer about four times a second while files move.
    public Action<Dictionary<string, Transfer>> Transfers = _ => { };
    public Dictionary<string, LinkState> Statuses { get; private set; } = new();
    public List<Hello> Linked { get; private set; } = new();

    public Engine(string? name = null)
    {
        Directory.CreateDirectory(Paths.Dir);
        var idFile = Path.Combine(Paths.Dir, "device-id");
        try { Device = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Device = ""; }
        if (Device.Length == 0)
        {
            Device = Guid.NewGuid().ToString().ToUpperInvariant();
            File.WriteAllText(idFile, Device);
        }
        Name = name ?? (Environment.GetEnvironmentVariable("CLIPBUBBLE_DEVICE_NAME") is { Length: > 0 } n ? n : Environment.MachineName);
        new Thread(Run) { IsBackground = true, Name = "ClipBubble sync" }.Start();
        Post(() => Db.Changed = hashes => Post(() => Broadcast(hashes)));
    }

    void Run()
    {
        db = new Db();
        foreach (var act in queue.GetConsumingEnumerable())
        {
            try { act(); }
            catch (Exception e) { SyncLog.Write("error: " + e); }   // one bad frame or file must not stop sync
        }
    }

    public void Post(Action act) => queue.Add(act);

    static void Background(string name, Action act) => new Thread(() => act()) { IsBackground = true, Name = name }.Start();

    // MARK: Hosts

    public void Start()
    {
        Post(ServePipe);
        Post(ReconcileLinks);
        Post(WatchOtherWriters);
    }

    /// Agents (ClipBubble.exe --mcp) write history from their own process. SQLite's data_version moves when another
    /// connection commits, so a once-a-second check costs nothing while nothing changes.
    void WatchOtherWriters()
    {
        long? version = null;
        var known = new Dictionary<string, Stamp>();
        void Check()
        {
            var v = db.DataVersion();
            if (v == version) return;
            var now = db.Stamps().ToDictionary(s => s.Hash);
            if (version != null && Sessions.Count > 0)
                Broadcast(now.Where(p => !known.TryGetValue(p.Key, out var k) || k != p.Value).Select(p => p.Key).ToList());
            (version, known) = (v, now);
        }
        Background("ClipBubble watch", () => { while (true) { Post(Check); Thread.Sleep(1000); } });
    }

    public static string[] Hosts => Prefs.Get<object?>("sync.hosts", null) is System.Collections.IEnumerable e and not string ? e.OfType<string>().ToArray() : Array.Empty<string>();
    public static bool Paused => Prefs.Get("sync.paused", false);

    public void ReconcileLinks()
    {
        var want = Paused ? new HashSet<string>() : new HashSet<string>(Hosts);
        foreach (var alias in links.Keys.ToList())
            if (!want.Contains(alias)) { links[alias].Stop(); links.Remove(alias); }
        foreach (var alias in want)
            if (!links.ContainsKey(alias))
            {
                var link = new HostLink(alias, this);
                links[alias] = link;
                link.Connect();
            }
        PublishStatus();
    }

    /// Tries every offline host now, like after waking from sleep.
    public void Redial() => Post(() => { foreach (var l in links.Values) l.Redial(); });

    internal void PublishStatus()
    {
        Statuses = links.ToDictionary(p => p.Key, p => p.Value.State);
        Linked = Sessions.Where(s => s.Peer != null).Select(s => s.Peer!).ToList();
        Status();
    }

    // MARK: Manual transfers

    /// Get: downloads an item that's still on another device, whatever its size.
    public void Get(string hash) => Post(() =>
    {
        wanted.Add(hash);
        if (Sessions.FirstOrDefault(s => s.Offered.ContainsKey(hash)) is { } s)
        {
            s.Download(MakeDownload(s.Offered[hash], s, false));
            WatchTransfers();
        }
    });

    /// Send to: asks a linked device to fetch one of this machine's items now.
    public void SendTo(string hash, string device) => Post(() =>
    {
        if (Sessions.FirstOrDefault(s => s.Summarized && s.Peer?.Device == device) is not { } s) return;
        if (db.Stamp(hash) is { } st && st.Beats(s.Known.TryGetValue(hash, out var k) ? k : null)) Send(st, s);
        s.Send(new Offer(hash));
    });

    /// Stops a transfer either way and throws away what arrived.
    public void CancelTransfer(string hash) => Post(() =>
    {
        wanted.Remove(hash);
        foreach (var s in Sessions.Where(s => s.IsTransferring(hash)).ToList())
        {
            s.CancelDownload(hash);
            s.CancelUpload(hash);
            s.Send(new Cancel(hash));
        }
        PublishTransfers();
    });

    Download MakeDownload(Entry e, Session s, bool apply) =>
        new(e.Hash, e.Kind, e.Blobs.Select(b => b.Name).ToList(), e.Blobs.Select(b => b.Sha).ToList(), e.Blobs.Select(b => b.Size).ToList(), s.Peer?.Name ?? "Device", apply);

    /// Progress ticks only while something is moving, so an idle link costs nothing.
    internal void WatchTransfers()
    {
        if (ticking) { PublishTransfers(); return; }
        ticking = true;
        Background("ClipBubble progress", () => { while (Volatile.Read(ref ticking)) { Post(PublishTransfers); Thread.Sleep(250); } });
    }

    void PublishTransfers()
    {
        var all = new Dictionary<string, Transfer>();
        foreach (var s in Sessions)
            foreach (var p in s.TransfersNow())
                if (!all.ContainsKey(p.Key)) all[p.Key] = p.Value;
        if (all.Count == 0) Volatile.Write(ref ticking, false);
        if (all.Count != lastTransfers.Count || all.Any(p => !lastTransfers.TryGetValue(p.Key, out var t) || t != p.Value))
        {
            lastTransfers = all;
            Transfers(all);
        }
    }

    /// The pipe that `ClipBubble.exe --peer` (run by ssh from another machine) connects to. Only this user can open it.
    public static string PipeName => "studio.krnl.clipbubble.sync." + Store.Sha(Paths.Dir.ToLowerInvariant()).Substring(0, 12);

    void ServePipe() => Background("ClipBubble accept", () =>
    {
        while (true)
        {
            NamedPipeServerStream pipe;
            try { pipe = NewPipe(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SyncLog.Write("can't listen for other devices: " + e.Message);
                return;
            }
            try { pipe.WaitForConnection(); }
            catch (IOException) { pipe.Dispose(); continue; }
            Post(() => Begin(new Session(pipe, pipe, this) { Abort = () => { try { pipe.Disconnect(); } catch (Exception) { } } }));
        }
    });

    static NamedPipeServerStream NewPipe()
    {
#if NETFRAMEWORK
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
#else
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
    }

    // MARK: Sessions

    internal void Begin(Session s)
    {
        if (Paused && s.Link == null) { s.Close(); return; }
        Sessions.Add(s);
        s.Start(new Hello(SyncProto.Version, Device, Name, "windows", Version, Now), db.Stamps(includingWaiting: false));
    }

    internal void Ended(Session s)
    {
        Sessions.Remove(s);
        s.Link?.SessionEnded(s);
        PublishStatus();
    }

    void Broadcast(IReadOnlyList<string> hashes)
    {
        var ready = Sessions.Where(s => s.Summarized).ToList();
        if (ready.Count == 0) return;
        foreach (var h in hashes.Distinct())
        {
            if (db.Stamp(h) is not { } st) continue;
            foreach (var s in ready)
                if (st.Beats(s.Known.TryGetValue(h, out var k) ? k : null)) Send(st, s);
        }
    }

    void Send(Stamp st, Session s)
    {
        if (st.Deleted)
        {
            s.Known[st.Hash] = st;
            s.Send(new Tombstone(st));
            return;
        }
        if (db.ByHash(st.Hash) is not { } c || !db.Allowed(c.Kind.Name(), c.Source) || EntryFor(c) is not { } e) return;
        s.Known[st.Hash] = st;
        if (c.Origin.Length == 0 && Now - c.Time < 10) sentAt[st.Hash] = Now;
        s.Send(e);
    }

    Entry? EntryFor(Clip c)
    {
        var origin = c.Origin.Length > 0 ? c.Origin : Device;
        var none = Array.Empty<Blob>();
        switch (c.Kind)
        {
            case ClipKind.Text:
                return new Entry(c.Hash, c.Time, "text", c.Text, c.Source, origin, none);
            case ClipKind.Image:
                if (c.Remote) return new Entry(c.Hash, c.Time, "image", "", c.Source, origin, none);
                if (c.File is not { } f || !File.Exists(f)) return null;
                var h = c.Hash.Substring(2);
                return new Entry(c.Hash, c.Time, "image", "", c.Source, origin, new[] { new Blob(h + ".png", new FileInfo(f).Length, h) });
            default:
                if (c.Remote) return new Entry(c.Hash, c.Time, "file", c.Text, c.Source, origin, none);
                var names = string.Join("\n", c.Paths.Select(Path.GetFileName));
                return new Entry(c.Hash, c.Time, "file", names, c.Source, origin,
                    BlobFiles(c).Select(p => new Blob(Path.GetFileName(p), new FileInfo(p).Length, "")).ToList());
        }
    }

    static List<string> BlobFiles(Clip c) =>
        c.Kind == ClipKind.Image ? (c.File is { } f ? new List<string> { f } : new()) : c.Paths.Where(File.Exists).ToList();

    // MARK: Frames

    internal void Handle(Frame frame, Session s)
    {
        switch (frame)
        {
            case Hello p:
                if (p.Proto != SyncProto.Version)
                {
                    SyncLog.Write($"{p.Name} speaks protocol {p.Proto}, this machine {SyncProto.Version}");
                    if (s.Link != null) s.Link.Mismatch = p.Version;
                    s.Close();
                    return;
                }
                if (p.Device == Device || Sessions.Any(o => o != s && o.Peer?.Device == p.Device)) { s.Close(); return; }
                s.Peer = p;
                s.Offset = p.Clock - Now;
                if (Math.Abs(s.Offset) > 30) SyncLog.Write($"{p.Name}'s clock is {(int)s.Offset} s off");
                db.SaveDevice(p.Device, p.Name, p.Os);
                s.Link?.HelloReceived(p);
                SyncLog.Write($"linked with {p.Name} ({p.Os} {p.Version})");
                PublishStatus();
                break;
            case Summary p:
                if (s.Peer == null) { s.Close(); return; }
                foreach (var st in p.Stamps)
                    if (st.Beats(s.Known.TryGetValue(st.Hash, out var k) ? k : null)) s.Known[st.Hash] = st;
                s.Summarized = true;
                var mine = SyncRules.Reconcile(db.Stamps(), p.Stamps);
                foreach (var st in mine.OrderBy(x => x.Time)) Send(st, s);
                SyncLog.Write($"reconciled with {s.Peer.Name}: sent {mine.Count} of {p.Stamps.Count} known there");
                break;
            case Entry p:
                Receive(p, s);
                break;
            case Tombstone t:
                var ts = t.Stamp;
                if (!ts.Deleted) { s.Close(); return; }
                if (ts.Beats(s.Known.TryGetValue(ts.Hash, out var known) ? known : null)) s.Known[ts.Hash] = ts;
                if (SyncRules.Merge(db.Stamp(ts.Hash), db.Exists(ts.Hash), ts) == MergeAction.Delete)
                {
                    s.CancelDownload(ts.Hash);
                    db.Tombstone(ts);
                    Received();
                }
                break;
            case Fetch p:
                var c = db.ByHash(p.Hash);
                var files = c != null && !c.Remote ? BlobFiles(c) : new List<string>();
                if (p.Index >= files.Count || !s.Upload(p.Hash, p.Index, files[p.Index], p.Offset)) s.Send(new Cancel(p.Hash));
                else WatchTransfers();
                break;
            case Chunk p:
                if (s.TakeChunk(p) is { } done) Finished(done, s);
                break;
            case Cancel p:
                s.CancelUpload(p.Hash);
                s.CancelDownload(p.Hash);
                break;
            case Applied p:
                if (sentAt.TryGetValue(p.Hash, out var at))
                {
                    sentAt.Remove(p.Hash);
                    SyncLog.Write($"applied on {s.Peer?.Name ?? "peer"} in {(int)((Now - at) * 1000)} ms");
                }
                break;
            case Ping p:
                s.Send(new Pong(p.At));
                break;
            case Offer p:
                if (s.Offered.TryGetValue(p.Hash, out var offered) && db.ByHash(p.Hash) is { Remote: true })
                {
                    s.Download(MakeDownload(offered, s, false));
                    WatchTransfers();
                }
                break;
        }
    }

    void Receive(Entry e, Session s)
    {
        var st = e.Stamp;
        if (st.Beats(s.Known.TryGetValue(e.Hash, out var k) ? k : null)) s.Known[e.Hash] = st;
        var now = Now;
        if (e.Time < now - Db.Retention) return;
        var hasRow = db.Exists(e.Hash);
        var newest = db.Clips(limit: 1).FirstOrDefault();
        var fresh = now - (e.Time - s.Offset) < 10 && e.Time > (newest?.Time ?? 0);
        switch (SyncRules.Merge(db.Stamp(e.Hash), hasRow, st))
        {
            case MergeAction.Delete:
                return;
            case MergeAction.Ignore:
                if (db.ByHash(e.Hash) is not { Remote: true }) return;
                break;
            case MergeAction.Bump:
                db.Bump(e.Hash, e.Time);
                if (fresh && db.ByHash(e.Hash) is { Remote: false } bumped) ApplyNow(bumped, s);
                break;
            default:
                if (!db.Allowed(e.Kind, e.Source)) return;
                db.Insert(e, e.Origin == Device ? "" : e.Origin);
                if (e.Blobs.Count == 0 && fresh && db.ByHash(e.Hash) is { } added) ApplyNow(added, s);
                break;
        }
        // Still missing its bytes, whether new here or left over from a link that dropped mid-download.
        if (e.Blobs.Count > 0 && db.ByHash(e.Hash) is { Remote: true })
        {
            s.Offered[e.Hash] = e;
            if (e.Size <= SyncProto.AutoFetchLimit || wanted.Contains(e.Hash))
            {
                s.Download(MakeDownload(e, s, fresh));
                WatchTransfers();
            }
        }
        Received();
    }

    void ApplyNow(Clip c, Session s)
    {
        Apply(c);
        s.Send(new Applied(c.Hash));
    }

    void Finished(Download d, Session s)
    {
        wanted.Remove(d.Hash);
        s.Offered.Remove(d.Hash);
        if (d.Kind == "image") db.Received(d.Hash, file: d.Paths[0]);
        else db.Received(d.Hash, text: string.Join("\n", d.Paths));
        if (d.Apply && db.Clips(limit: 1).FirstOrDefault() is { } newest && newest.Hash == d.Hash) ApplyNow(newest, s);
        Received();
    }

    // MARK: Files

    /// A name from another machine can't climb out of its folder, hide itself or name a device.
    public static string SafeFileName(string raw)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("/\\:*?\"<>|\r\n\0"));
        var s = new string(raw.Select(ch => bad.Contains(ch) || ch < ' ' ? '_' : ch).ToArray());
        s = s.TrimStart('.');
        if (s.Length > 200) s = s.Substring(0, 200);
        s = s.Trim().TrimEnd('.', ' ');
        var stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) s = "_" + s;
        return s.Length > 0 ? s : "file";
    }

    public static string ReceivedFolder(string device) => Path.Combine(Downloads(), "ClipBubble", SafeFileName(device));

    public static string UniquePath(string folder, string name)
    {
        var p = Path.Combine(folder, name);
        var (stem, ext) = (Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        for (var n = 2; File.Exists(p) || Directory.Exists(p); n++) p = Path.Combine(folder, $"{stem} ({n}){ext}");
        return p;
    }

    // MARK: Hosts from ~/.ssh/config

    /// Host aliases from ~/.ssh/config and the files it includes, skipping patterns.
    public static List<string> SshHosts()
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var seen = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string path)
        {
            if (!visited.Add(path)) return;
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return; }
            foreach (var line in lines)
            {
                var parts = line.Replace('=', ' ').Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var key = parts[0].ToLowerInvariant();
                if (key == "host")
                {
                    foreach (var h in parts.Skip(1))
                        if (h.IndexOfAny(new[] { '*', '?' }) < 0 && !h.StartsWith("!") && !seen.Contains(h)) seen.Add(h);
                }
                else if (key == "include")
                    foreach (var pattern in parts.Skip(1))
                    {
                        var p = pattern.StartsWith("~") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + pattern.Substring(1) : pattern;
                        if (!Path.IsPathRooted(p)) p = Path.Combine(ssh, p);
                        var dir = Path.GetDirectoryName(p);
                        if (dir == null || !Directory.Exists(dir)) continue;
                        foreach (var f in Directory.GetFiles(dir, Path.GetFileName(p)).OrderBy(x => x, StringComparer.Ordinal)) Read(f);
                    }
            }
        }
        Read(Path.Combine(ssh, "config"));
        return seen;
    }
}

/// One host from ~/.ssh/config: runs ssh, retries with backoff, turns ssh's errors into a status.
sealed class HostLink(string alias, Engine engine)
{
    const string Posix = "sh -c 'for p in \"$HOME/.local/bin/clipbubble\" \"$HOME/Applications/ClipBubble.app/Contents/MacOS/ClipBubble\" " +
        "/Applications/ClipBubble.app/Contents/MacOS/ClipBubble; do [ -x \"$p\" ] && exec \"$p\" --peer; done; " +
        "echo CLIPBUBBLE_NOT_INSTALLED >&2; exit 127'";
    const string Windows = "\"%LOCALAPPDATA%\\Programs\\ClipBubble\\ClipBubble.exe\" --peer";

    Process? proc;
    Session? session;
    readonly StringBuilder stderr = new();
    double backoff = 1;
    Timer? timer;
    bool stopped;
    internal string? Mismatch;
    public LinkState State { get; private set; } = new("connecting");

    bool IsWindows => Prefs.Get<string?>("sync.os." + alias, null) == "windows";

    /// Windows' own OpenSSH client, wherever PATH points.
    static string SshExe =>
        Environment.GetEnvironmentVariable("CLIPBUBBLE_SSH") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe") is var builtin && File.Exists(builtin) ? builtin
        : "ssh";

    public void Connect()
    {
        if (stopped || proc != null || alias.StartsWith("-")) return;
        Set(new LinkState("connecting"));
        var args = new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2", "--", alias, IsWindows ? Windows : Posix };
        var info = new ProcessStartInfo(SshExe, string.Join(" ", args.Select(Quote)))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
#if NETFRAMEWORK
        // .NET Framework wraps ssh's stdin in a writer with the console's encoding, and writes that encoding's byte
        // order mark the moment ssh starts: with UTF-8 as the system code page, that would garble the first frame.
        // A UTF-16 encoding without a mark is the one setting Windows accepts without a console.
        Console.InputEncoding = new UnicodeEncoding(false, false);
#else
        info.StandardInputEncoding = new UTF8Encoding(false);
#endif
        Process p;
        try { p = Process.Start(info)!; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Set(new LinkState("offline", At: Engine.Now + backoff));
            Retry();
            return;
        }
        proc = p;
        lock (stderr) stderr.Clear();
        new Thread(() =>
        {
            try
            {
                while (p.StandardError.ReadLine() is { } line)
                    lock (stderr) if (stderr.Length < 8192) stderr.AppendLine(line);
            }
            catch (IOException) { }
            p.WaitForExit();
            engine.Post(() => Exited(p));
        }) { IsBackground = true }.Start();
        session = new Session(p.StandardOutput.BaseStream, p.StandardInput.BaseStream, engine) { Link = this };
        engine.Begin(session);
    }

    /// Windows command-line quoting, which ssh.exe parses back into the arguments above.
    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        for (var i = 0; i < a.Length; i++)
        {
            var slashes = 0;
            while (i < a.Length && a[i] == '\\') { slashes++; i++; }
            if (i == a.Length) { sb.Append('\\', slashes * 2); break; }
            if (a[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(a[i]);
        }
        return sb.Append('"').ToString();
    }

    public void HelloReceived(Hello h)
    {
        backoff = 1;
        Mismatch = null;
        Set(new LinkState("live", h.Name));
    }

    public void SessionEnded(Session s)
    {
        if (session != s) return;
        session = null;
        Kill();
    }

    void Kill()
    {
        try { if (proc is { HasExited: false } p) p.Kill(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    void Exited(Process p)
    {
        if (p != proc) return;
        var wasLive = State.Kind == "live";
        proc = null;
        p.Dispose();
        if (session is { } s) { session = null; s.Close(); }
        if (stopped) return;
        string err;
        lock (stderr) err = stderr.ToString();
        if (!IsWindows && (err.Contains("is not recognized") || err.Contains("CommandNotFoundException")))
        {
            Prefs.Set("sync.os." + alias, "windows");
            Connect();
            return;
        }
        LinkState next =
            Mismatch != null ? new("version", Mismatch)
            : err.Contains("CLIPBUBBLE_NOT_INSTALLED") || (IsWindows && err.Contains("cannot find the path")) ? new("not-installed")
            : err.Contains("CLIPBUBBLE_NOT_RUNNING") ? new("not-running")
            : err.Contains("Host key verification failed") ? new("unknown-host")
            : err.Contains("Permission denied") || err.Contains("Too many authentication failures") ? new("needs-key")
            : new("offline", At: Engine.Now + (wasLive ? 1 : backoff));
        if (err.Trim().Length > 0) SyncLog.Write($"{alias}: {Tail(err.Trim(), 300)}");
        if (next.Kind != "offline") backoff = Math.Max(backoff, 60);
        if (wasLive) backoff = 1;
        Set(next);
        Retry();
    }

    static string Tail(string s, int n) => s.Length <= n ? s : s.Substring(s.Length - n);

    void Retry()
    {
        var delay = backoff;
        backoff = Math.Min(backoff * 2, 300);
        timer?.Dispose();
        timer = new Timer(_ => engine.Post(Connect), null, TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan);
    }

    public void Redial()
    {
        if (stopped || proc != null) return;
        timer?.Dispose();
        backoff = 1;
        Connect();
    }

    public void Stop()
    {
        stopped = true;
        timer?.Dispose();
        session?.Close();
        Kill();
    }

    void Set(LinkState s)
    {
        State = s;
        engine.PublishStatus();
    }
}

/// One peer. Reads frames on a thread, writes on another with control frames ahead of file chunks.
sealed class Session(Stream input, Stream output, Engine engine)
{
    internal HostLink? Link;
    /// Wakes the reader on close: a pipe disconnects; an ssh pipe ends when its link stops ssh.
    internal Action? Abort;
    internal Action OnClose = () => { };
    internal Hello? Peer;
    internal double Offset;
    internal bool Summarized;
    internal readonly Dictionary<string, Stamp> Known = new();
    /// Items the peer has bytes for that haven't come over yet, for Get and Send to.
    internal readonly Dictionary<string, Entry> Offered = new();
    readonly FrameReader reader = new();
    readonly Dictionary<string, Download> downloads = new();
    readonly LinkedList<Download> pending = new();
    readonly object gate = new();
    readonly Queue<byte[]> control = new();
    readonly List<Upload> uploads = new();
    bool closed;

    public void Start(Hello hello, List<Stamp> summary)
    {
        new Thread(Read) { IsBackground = true, Name = "ClipBubble read" }.Start();
        new Thread(Write) { IsBackground = true, Name = "ClipBubble write" }.Start();
        Send(hello);
        Send(new Summary(summary));
    }

    void Read()
    {
        var buf = new byte[65536];
        while (true)
        {
            int n;
            try { n = input.Read(buf, 0, buf.Length); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { n = 0; }
            if (n <= 0)
            {
                engine.Post(Close);
                return;
            }
            var data = new byte[n];
            Array.Copy(buf, data, n);
            engine.Post(() => Feed(data));
        }
    }

    void Feed(byte[] data)
    {
        if (closed) return;
        List<Frame> frames;
        try { frames = reader.Feed(data, data.Length); }
        catch (Malformed)
        {
            SyncLog.Write($"dropped {Peer?.Name ?? "peer"}: malformed frame");
            Close();
            return;
        }
        foreach (var f in frames)
        {
            if (closed) return;
            engine.Handle(f, this);
        }
    }

    public void Send(Frame f)
    {
        var data = Codec.Encode(f);
        lock (gate)
        {
            control.Enqueue(data);
            Monitor.PulseAll(gate);
        }
    }

    byte[]? Next()
    {
        lock (gate)
            while (true)
            {
                if (closed) return null;
                if (control.Count > 0) return control.Dequeue();
                if (uploads.Count > 0)
                {
                    var u = uploads[0];
                    if (u.NextChunk() is not { } frame)
                    {
                        uploads.RemoveAt(0);
                        u.Dispose();
                        return Codec.Encode(new Cancel(u.Hash));
                    }
                    if (u.Done) { uploads.RemoveAt(0); u.Dispose(); }
                    return Codec.Encode(frame);
                }
                Monitor.Wait(gate);
            }
    }

    void Write()
    {
        try
        {
            while (Next() is { } data)
            {
                output.Write(data, 0, data.Length);
                output.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { engine.Post(Close); }
        try { output.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public bool Upload(string hash, int index, string path, long offset)
    {
        Upload u;
        try { u = new Upload(hash, index, path, offset); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        lock (gate)
        {
            uploads.Add(u);
            Monitor.PulseAll(gate);
        }
        return true;
    }

    public void CancelUpload(string hash)
    {
        lock (gate)
            foreach (var u in uploads.Where(u => u.Hash == hash).ToList()) { uploads.Remove(u); u.Dispose(); }
    }

    public bool IsTransferring(string hash)
    {
        bool up;
        lock (gate) up = uploads.Any(u => u.Hash == hash);
        return up || downloads.ContainsKey(hash) || pending.Any(d => d.Hash == hash);
    }

    public Dictionary<string, Transfer> TransfersNow()
    {
        var name = Peer?.Name ?? "Device";
        var output = downloads.Values.Concat(pending).ToDictionary(d => d.Hash, d => new Transfer(d.DoneBytes, d.Total, name, false));
        lock (gate)
            foreach (var u in uploads) output[u.Hash] = new Transfer(u.Offset, u.Total, name, true);
        return output;
    }

    /// One download at a time per peer; the rest wait their turn.
    public void Download(Download d)
    {
        if (downloads.ContainsKey(d.Hash) || pending.Any(p => p.Hash == d.Hash)) return;
        if (downloads.Count == 0) Begin(d);
        else pending.AddLast(d);
    }

    void Begin(Download d)
    {
        downloads[d.Hash] = d;
        Send(new Fetch(d.Hash, d.Index, d.ReceivedBytes));
    }

    public void CancelDownload(string hash)
    {
        foreach (var p in pending.Where(p => p.Hash == hash).ToList()) pending.Remove(p);
        if (downloads.TryGetValue(hash, out var d))
        {
            downloads.Remove(hash);
            d.Discard();
            StartNext();
        }
    }

    void StartNext()
    {
        if (downloads.Count == 0 && pending.First is { } next)
        {
            pending.RemoveFirst();
            Begin(next.Value);
        }
    }

    /// The finished download, once its last chunk lands.
    public Download? TakeChunk(Chunk c)
    {
        if (!downloads.TryGetValue(c.Hash, out var d) || c.Index != d.Index || c.Offset != d.ReceivedBytes) return null;
        switch (d.Write(c.Data, c.Total))
        {
            case Step.More:
                return null;
            case Step.Failed:
                SyncLog.Write("couldn't save a file from " + d.Source);
                downloads.Remove(c.Hash);
                StartNext();
                return null;
            case Step.Next:
                Send(new Fetch(c.Hash, d.Index, 0));
                return null;
            default:
                downloads.Remove(c.Hash);
                StartNext();
                return d;
        }
    }

    public void Close()
    {
        if (closed) return;
        lock (gate)
        {
            closed = true;
            foreach (var u in uploads) u.Dispose();
            uploads.Clear();
            Monitor.PulseAll(gate);
        }
        Abort?.Invoke();
        // Partial files stay, so the next link resumes them from where this one stopped.
        foreach (var d in downloads.Values) d.Suspend();
        downloads.Clear();
        engine.Ended(this);
        OnClose();
    }
}

sealed class Upload : IDisposable
{
    public readonly string Hash;
    readonly int index;
    readonly FileStream f;
    public readonly long Total;
    public long Offset;
    bool sentEmpty;

    public Upload(string hash, int index, string path, long offset)
    {
        Hash = hash;
        this.index = index;
        f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Total = f.Length;
        if (offset < 0 || offset > Total) { f.Dispose(); throw new ArgumentException("bad offset"); }
        f.Position = Offset = offset;
    }

    public bool Done => Offset >= Total && (Total > 0 || sentEmpty);

    public Chunk? NextChunk()
    {
        var n = (int)Math.Min(SyncProto.ChunkSize, Total - Offset);
        var data = new byte[n];
        var got = 0;
        try { while (got < n && f.Read(data, got, n - got) is var r && r > 0) got += r; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return null; }
        if (got != n) return null;
        var chunk = new Chunk(Hash, index, Offset, Total, data);
        Offset += n;
        if (n == 0) sentEmpty = true;
        return chunk;
    }

    public void Dispose() => f.Dispose();
}

enum Step { More, Next, Done, Failed }

sealed class Download
{
    public readonly string Hash, Kind, Source;
    readonly List<string> names, shas;
    readonly List<long> sizes;
    public readonly bool Apply;
    public int Index;
    public long ReceivedBytes;
    public readonly List<string> Paths = new();
    FileStream? f;

    public Download(string hash, string kind, List<string> names, List<string> shas, List<long> sizes, string source, bool apply)
    {
        (Hash, Kind, this.names, this.shas, this.sizes, Source, Apply) = (hash, kind, names, shas, sizes, source, apply);
        // A partial first file from a link that dropped: carry on from its end.
        try
        {
            if (File.Exists(Temp) && new FileInfo(Temp).Length is var size && size <= (sizes.Count > 0 ? sizes[0] : 0)) ReceivedBytes = size;
        }
        catch (IOException) { }
    }

    public long Total => sizes.Sum();
    public long DoneBytes => sizes.Take(Index).Sum() + ReceivedBytes;
    string Temp => Path.Combine(ClipBubble.Paths.Dir, "incoming", $"{Store.Sha(Hash)}-{Index}");

    public Step Write(byte[] data, long total)
    {
        try
        {
            if (Index >= names.Count || ReceivedBytes + data.Length > total) return Step.Failed;
            if (f == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Temp)!);
                f = new FileStream(Temp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                f.SetLength(ReceivedBytes);
                f.Position = ReceivedBytes;
            }
            f.Write(data, 0, data.Length);
            ReceivedBytes += data.Length;
            if (ReceivedBytes != total) return Step.More;
            f.Dispose();
            f = null;
            if (Place() is not { } dest) return Step.Failed;
            Paths.Add(dest);
            Index++;
            ReceivedBytes = 0;
            return Index < names.Count ? Step.Next : Step.Done;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Suspend();
            return Step.Failed;
        }
    }

    string? Place()
    {
        if (Kind == "image")
        {
            var sha = Store.Sha(File.ReadAllBytes(Temp));
            var want = Index < shas.Count ? shas[Index] : "";
            if (want.Length > 0 && sha != want) { File.Delete(Temp); return null; }
            var dest = Path.Combine(ClipBubble.Paths.Images, sha + ".png");
            if (File.Exists(dest)) File.Delete(Temp);
            else File.Move(Temp, dest);
            return dest;
        }
        var folder = Engine.ReceivedFolder(Source);
        Directory.CreateDirectory(folder);
        var to = Engine.UniquePath(folder, Engine.SafeFileName(names[Index]));
        File.Move(Temp, to);
        return to;
    }

    public void Suspend()
    {
        f?.Dispose();
        f = null;
    }

    public void Discard()
    {
        Suspend();
        try { File.Delete(Temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// `ClipBubble.exe --peer`, run by ssh from another machine: pipes ssh's stdio to the running app.
/// The app isn't started from here: under ssh it would run on no one's desktop.
static class Relay
{
    public static int Run()
    {
        NamedPipeClientStream? pipe = null;
        for (var i = 0; i < 10 && pipe == null; i++)
        {
            var p = new NamedPipeClientStream(".", Engine.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { p.Connect(250); pipe = p; }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { p.Dispose(); Thread.Sleep(250); }
        }
        if (pipe == null)
        {
            using var err = Console.OpenStandardError();
            var msg = Encoding.ASCII.GetBytes("CLIPBUBBLE_NOT_RUNNING\n");
            err.Write(msg, 0, msg.Length);
            return 3;
        }
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        new Thread(() => { Pump(stdin, pipe); try { pipe.Dispose(); } catch (Exception) { } }) { IsBackground = true }.Start();
        Pump(pipe, stdout);
        return 0;
    }

    static void Pump(Stream from, Stream to)
    {
        var buf = new byte[65536];
        try
        {
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                to.Write(buf, 0, n);
                to.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
}
