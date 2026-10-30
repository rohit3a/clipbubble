using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ClipBubble.SyncTests;

/// The Windows sync code on any OS: the shared wire fixtures, the store's sync side, and a live session with the
/// Mac engine (the Swift tests binary). Exit code is the number of failures.
static class Program
{
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    static readonly string Home = Path.Combine(Path.GetTempPath(), "clipbubble-synctests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--peer") return Relay.Run();
        Environment.SetEnvironmentVariable("CLIPBUBBLE_HOME", Home);
        Environment.SetEnvironmentVariable("CLIPBUBBLE_DOWNLOADS", Path.Combine(Home, "Downloads"));
        Directory.CreateDirectory(Home);
        var failures = 0;
        try
        {
            foreach (var (name, test) in All())
            {
                try { test(); Console.WriteLine("ok   " + name); }
                catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + "\n     " + e.Message); }
            }
            Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
            return failures;
        }
        finally
        {
            try { Directory.Delete(Home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    internal static bool WaitFor(Func<bool> cond, double seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (cond()) return true; Thread.Sleep(50); }
        return false;
    }

    static readonly Blob[] None = Array.Empty<Blob>();

    /// Same names and values as Linux/tests/test_sync.py and the Swift fixture writer.
    static readonly Dictionary<string, Frame> Frames = new()
    {
        ["hello"] = new Hello(1, "3F2A-mac", "Rohit's MacBook", "macos", "1.4.0", 1792000000.25),
        ["summary"] = new Summary(new[] { new Stamp("t:abc", 1792000000.5, false), new Stamp("i:def", 1791999999, true) }),
        ["summary-empty"] = new Summary(Array.Empty<Stamp>()),
        ["put-text"] = new Entry("t:01", 1792000001, "text", "héllo\nwörld 👋", "Safari", "3F2A-mac", None),
        ["put-image"] = new Entry("i:ff", 1792000002, "image", "", "Figma", "rog", new[] { new Blob("ff.png", 1234, "ff") }),
        ["put-files"] = new Entry("f:9", 1792000003, "file", "a.pdf\nb c.zip", "Finder", "3F2A-mac",
            new[] { new Blob("a.pdf", 10, ""), new Blob("b c.zip", 30_000_000_000, "") }),
        ["tombstone"] = new Tombstone(new Stamp("t:01", 1792000004, true)),
        ["fetch"] = new Fetch("f:9", 1, 262144),
        ["chunk"] = new Chunk("f:9", 0, 0, 10, new byte[] { 0, 1, 2, 255, 254, 10, 13, 0, 7, 9 }),
        ["cancel"] = new Cancel("f:9"),
        ["applied"] = new Applied("t:01"),
        ["ping"] = new Ping(1792000005.125),
        ["pong"] = new Pong(1792000005.125),
        ["offer"] = new Offer("f:9"),
        ["device"] = new DeviceInfo("ad03-rog", "ROG Strix ☕", "linux"),
    };

    static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
    static byte[] Unhex(string s) => Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();

    static bool Throws(Action act)
    {
        try { act(); return false; }
        catch (Malformed) { return true; }
    }

    static IEnumerable<(string, Action)> All() => new (string, Action)[]
    {
        ("QR codes match the ones a phone read", () =>
        {
            // Apple's Vision framework decoded each fixture when it was made, and Linux's encoder matches it too.
            foreach (var f in JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Tests/fixtures/qr/codes.json"))).RootElement.EnumerateArray())
            {
                var m = Qr.Encode(f.GetProperty("text").GetString()!);
                var rows = Enumerable.Range(0, m.GetLength(0)).Select(y => string.Concat(Enumerable.Range(0, m.GetLength(1)).Select(x => m[y, x] ? "1" : "0")));
                Check(rows.SequenceEqual(f.GetProperty("rows").EnumerateArray().Select(r => r.GetString())), "encodes " + f.GetProperty("text").GetString()![..5]);
            }
        }),

        ("frames match the Swift bytes", () =>
        {
            var committed = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Tests/fixtures/sync/frames.json"))).RootElement
                .EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("hex").GetString()!);
            Check(committed.Keys.OrderBy(k => k).SequenceEqual(Frames.Keys.OrderBy(k => k)), "same fixture names");
            foreach (var (name, frame) in Frames)
            {
                Check(Hex(Codec.Encode(frame)) == committed[name], "encodes " + name);
                var back = new FrameReader().Feed(Unhex(committed[name]), committed[name].Length / 2);
                Check(back.Count == 1 && back[0].Equals(frame), "decodes " + name);
            }
        }),

        ("frames split across reads", () =>
        {
            var data = Frames.Values.SelectMany(Codec.Encode).ToArray();
            var r = new FrameReader();
            var got = new List<Frame>();
            for (var i = 0; i < data.Length; i += 7)
            {
                var piece = data.Skip(i).Take(7).ToArray();
                got.AddRange(r.Feed(piece, piece.Length));
            }
            Check(got.SequenceEqual(Frames.Values), "all frames come back in order");
        }),

        ("bad input is rejected", () =>
        {
            Check(Throws(() => new FrameReader().Feed(new byte[] { 0x7f, 0xff, 0xff, 0xff }, 4)), "oversized length");
            Check(Throws(() => new FrameReader().Feed(new byte[4], 4)), "zero length");
            var body = Codec.Encode(new Ping(1)).Skip(4).ToArray();
            foreach (var bad in new[] { body.Take(body.Length - 1).ToArray(), body.Concat(new byte[1]).ToArray(), new byte[] { 0x63 },
                         new byte[] { 0x09 }.Concat(Unhex("7ff8000000000000")).ToArray(), new byte[] { 0x02, 0xff, 0xff, 0xff, 0xff } })
                Check(Throws(() => Codec.Decode(bad)), "rejects " + Hex(bad));
        }),

        ("fuzzed frames never crash", () =>
        {
            var rnd = new Random(7);
            var seeds = Frames.Values.Select(f => Codec.Encode(f).Skip(4).ToArray()).ToList();
            for (var n = 0; n < 3000; n++)
            {
                var b = (byte[])seeds[rnd.Next(seeds.Count)].Clone();
                for (var k = rnd.Next(1, 5); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                try { Codec.Decode(b); } catch (Malformed) { }
            }
        }),

        ("merge rules", () =>
        {
            var a = new Stamp("t:x", 10, false);
            Check(SyncRules.Merge(null, false, a) == MergeAction.Insert, "new item inserts");
            Check(SyncRules.Merge(new Stamp("t:x", 5, false), true, a) == MergeAction.Bump, "newer copy bumps");
            Check(SyncRules.Merge(new Stamp("t:x", 20, false), true, a) == MergeAction.Ignore, "older copy is ignored");
            Check(SyncRules.Merge(a, true, new Stamp("t:x", 10, true)) == MergeAction.Delete, "a delete wins a tie");
            Check(SyncRules.Merge(new Stamp("t:x", 10, true), false, a) == MergeAction.Ignore, "a copy loses a tie to a delete");
            var mine = SyncRules.Reconcile(new[] { a, new Stamp("t:y", 3, false) }, new[] { new Stamp("t:x", 10, false), new Stamp("t:y", 2, true) });
            Check(mine.Count == 1 && mine[0].Hash == "t:y", "reconcile sends only what beats the peer");
        }),

        ("an older database gains the sync columns", () =>
        {
            var path = Paths.Db;
            using (var raw = new RawDb(path))
                raw.Exec(@"CREATE TABLE clips(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
                           file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL);
                           INSERT INTO clips(kind,text,source,hash,created) VALUES('text','old','Notepad','t:old'," + (Engine.Now - 60) + ")");
            using var db = new Db();
            var c = db.ByHash("t:old");
            Check(c != null && c.Origin == "" && !c.Remote && Math.Abs(c.Time - (Engine.Now - 60)) < 2, "old row reads with defaults");
            Check(db.Scalar("PRAGMA user_version") == 1, "user_version 1");
            using (new Db()) { }   // opening again doesn't try to add the columns twice
            db.DeleteAll();
        }),

        ("deletes are remembered and beat older copies", () =>
        {
            using var db = new Db();
            var changed = new List<string>();
            Db.Changed = h => { lock (changed) changed.AddRange(h); };
            db.Upsert(ClipKind.Text, "gone", null, "Notepad", "t:gone");
            db.Delete("t:gone");
            Check(db.Stamp("t:gone") is { Deleted: true }, "a delete leaves a tombstone");
            db.InsertIfMissing(ClipKind.File, "C:\\x.png", "Screenshot", "t:gone", DateTime.Now.AddMinutes(-5));
            Check(!db.Exists("t:gone"), "an older backfill doesn't bring it back");
            Check(changed.Count(h => h == "t:gone") == 2, "insert and delete both reported");
            Check(db.Stamps().Any(s => s.Hash == "t:gone" && s.Deleted), "summaries carry the delete");
            Db.Changed = null;
            db.DeleteAll();
        }),

        ("device frames go to 1.6.2 and later, and saving a device reports news", () =>
        {
            bool Reads(string v) => new Hello(1, "", "", "", v, 0).ReadsDevices;
            Check(!Reads("1.6.1") && !Reads("1.5.0") && !Reads("1.6"), "releases before 1.6.2 close the link on them");
            Check(Reads("1.6.2") && Reads("1.6.10") && Reads("1.7") && Reads("2.0.0") && Reads("dev"), "1.6.2, later and dev builds read them");
            using var db = new Db();
            Check(db.SaveDevice("dev-3", "ROG", "linux"), "a new device is news");
            Check(!db.SaveDevice("dev-3", "ROG", "linux"), "the same device again isn't");
            Check(db.SaveDevice("dev-3", "ROG 2", "linux"), "a new name is news");
            Check(db.Devices().Contains(new DeviceInfo("dev-3", "ROG 2", "linux")), "devices come back with their OS");
        }),

        ("synced rows: waiting, bump, tombstone, received", () =>
        {
            using var db = new Db();
            var now = Engine.Now;
            db.SaveDevice("mac-1", "Mac", "macos");
            db.Insert(new Entry("i:aa", now - 5, "image", "", "Figma", "mac-1", new[] { new Blob("aa.png", 10, "aa") }), "mac-1");
            db.Insert(new Entry("t:hi", now - 4, "text", "hi", "Safari", "mac-1", None), "mac-1");
            Check(db.ByHash("i:aa") is { Remote: true, Origin: "mac-1" }, "an image waits for its bytes");
            Check(!db.Stamps(includingWaiting: false).Any(s => s.Hash == "i:aa"), "waiting rows stay out of the summary");
            Check(db.Clips(source: "Mac").Count == 2, "a device's name finds what came from it");
            db.Bump("t:hi", now - 1);
            Check(Math.Abs(db.ByHash("t:hi")!.Time - (now - 1)) < 1e-6, "bump moves it up");
            db.Tombstone(new Stamp("t:hi", now - 2, true));
            Check(db.Exists("t:hi"), "a delete from before the copy doesn't remove it");
            db.Tombstone(new Stamp("t:hi", now, true));
            Check(!db.Exists("t:hi"), "a later delete does");
            db.Received("i:aa", file: "/tmp/aa.png");
            Check(db.ByHash("i:aa") is { Remote: false, File: "/tmp/aa.png" }, "received bytes clear remote");
            Prefs.SetOn("images", false);
            Check(!db.Allowed("image", "Figma") && db.Allowed("text", "Safari"), "switched-off categories aren't allowed");
            Prefs.SetOn("images", true);
            db.DeleteAll();
        }),

        ("names from other machines stay in their folder", () =>
        {
            Check(Engine.SafeFileName("../x") == "_x", "no climbing out: " + Engine.SafeFileName("../x"));
            Check(Engine.SafeFileName("a\\b:c") == "a_b_c", "separators");
            Check(Engine.SafeFileName("CON.txt") == "_CON.txt", "device names");
            Check(Engine.SafeFileName("...") == "file", "nothing left");
            Check(Engine.SafeFileName("ok name.pdf") == "ok name.pdf", "normal names stay");
        }),

        ("a peer reaches only what it was offered", Boundary.Run),
        ("a live session with the Mac engine", SwiftPeer),
        ("--peer relays ssh to the running app", RelayToApp),
        ("a host link runs ssh and says how it's doing", HostLinkStates),
    };

    /// HostLink with a stand-in for ssh: it logs its arguments, then either serves the Mac engine on stdio or fails
    /// the way ssh does without a key.
    static void HostLinkStates()
    {
        var swift = Path.Combine(Root, ".build/tests");
        if (OperatingSystem.IsWindows() || !File.Exists(swift)) { Console.WriteLine("skip (needs the Swift tests binary and a POSIX shell)"); return; }
        var peerHome = Path.Combine(Home, "ssh-peer");
        Directory.CreateDirectory(peerHome);
        var argsLog = Path.Combine(Home, "ssh-args");
        var fake = Path.Combine(Home, "fake-ssh");
        File.WriteAllText(fake, $"#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{argsLog}'\n" +
            "[ \"$FAKE_SSH\" = deny ] && { echo 'rohit@host: Permission denied (publickey).' >&2; exit 255; }\n" +
            $"CLIPBUBBLE_HOME='{peerHome}' CLIPBUBBLE_DEVICE_NAME=Mac CLIPBUBBLE_CHILD=serve exec '{swift}'\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("CLIPBUBBLE_SSH", fake);

        var e = new Engine("Windows test");
        LinkState? State() => e.Statuses.TryGetValue("fakehost", out var st) ? st : null;
        Prefs.Set("sync.hosts", new[] { "fakehost" });
        e.Post(e.ReconcileLinks);
        Check(WaitFor(() => State() is { Kind: "live", Arg: "Mac" }), "live with the Mac: " + State()?.Text);
        var args = File.ReadAllLines(argsLog);
        Check(args.Take(2).SequenceEqual(new[] { "-T", "-o" }) && args[^2] == "fakehost" && args[^1].StartsWith("sh -c 'for p in \"$HOME/.local/bin/clipbubble\""),
            "ssh gets the host and the remote command as single arguments: " + string.Join(" | ", args));

        Prefs.Set("sync.hosts", Array.Empty<string>());
        e.Post(e.ReconcileLinks);
        Check(WaitFor(() => State() == null && e.Linked.Count == 0), "switching the host off drops the link");

        Environment.SetEnvironmentVariable("FAKE_SSH", "deny");
        Prefs.Set("sync.hosts", new[] { "fakehost" });
        e.Post(e.ReconcileLinks);
        Check(WaitFor(() => State() is { Kind: "needs-key" }), "no key: " + State()?.Text);
        Check(State()!.Text == "Needs SSH key login", "said plainly");
        Prefs.Set("sync.hosts", Array.Empty<string>());
        e.Post(e.ReconcileLinks);
        Environment.SetEnvironmentVariable("FAKE_SSH", null);
        Environment.SetEnvironmentVariable("CLIPBUBBLE_SSH", null);
    }

    /// What ssh runs on this machine when another one links here: `--peer` finds the app's pipe and pipes stdio to it.
    static void RelayToApp()
    {
        Process Peer()
        {
            var info = new ProcessStartInfo("dotnet", $"\"{typeof(Program).Assembly.Location}\" --peer")
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
            info.Environment["CLIPBUBBLE_HOME"] = Home;
            return Process.Start(info)!;
        }

        using (var down = Peer())
        {
            Check(down.WaitForExit(30_000) && down.ExitCode == 3, "exit 3 with no app running");
            Check(down.StandardError.ReadToEnd().Contains("CLIPBUBBLE_NOT_RUNNING"), "says the app isn't running");
        }

        var e = new Engine("Windows test");
        e.Start();
        Thread.Sleep(300);
        using var p = Peer();
        var hello = Codec.Encode(new Hello(SyncProto.Version, "relay-peer", "Relay", "linux", "1.4.0", Engine.Now));
        var summary = Codec.Encode(new Summary(Array.Empty<Stamp>()));
        p.StandardInput.BaseStream.Write(hello.Concat(summary).ToArray());
        p.StandardInput.BaseStream.Flush();
        var reader = new FrameReader();
        var got = new List<Frame>();
        var buf = new byte[65536];
        var read = new Thread(() =>
        {
            int n;
            while (!got.OfType<Summary>().Any() && (n = p.StandardOutput.BaseStream.Read(buf, 0, buf.Length)) > 0)
                lock (got) got.AddRange(reader.Feed(buf, n));
        }) { IsBackground = true };
        read.Start();
        Check(read.Join(10_000), "the app answers through the relay");
        Check(got.OfType<Hello>().SingleOrDefault() is { Os: "windows", Name: "Windows test" }, "its hello");
        Check(WaitFor(() => e.Linked.Any(h => h.Name == "Relay")), "the app sees the peer");
        p.StandardInput.Close();
        Check(p.WaitForExit(10_000), "the relay exits when ssh closes");
        Check(WaitFor(() => e.Linked.Count == 0), "the app drops the session");
    }

    static string Sha(string s) => Store.Sha(s);
    static string Sha(byte[] b) => Store.Sha(b);

    /// Mirrors Linux/tests/test_interop.py, with this engine in place of the Linux one.
    static void SwiftPeer()
    {
        var swift = Path.Combine(Root, ".build/tests");
        if (!File.Exists(swift)) { Console.WriteLine("skip (build the Swift tests first: ./tests.sh)"); return; }
        var peerHome = Path.Combine(Home, "swift-peer");
        Directory.CreateDirectory(peerHome);
        var big = Path.Combine(Home, "big file.bin");
        var bigData = Enumerable.Range(0, 600_000).Select(i => (byte)((i * 31) & 0xFF)).ToArray();
        File.WriteAllBytes(big, bigData);
        var liveThere = $"mac live {Engine.Now}";
        var huge = Path.Combine(Home, "huge.bin");
        var hugeHere = Path.Combine(Home, "huge here.bin");
        var hugeData = new byte[26 << 20];
        new Random(5).NextBytes(hugeData);
        File.WriteAllBytes(huge, hugeData);
        File.WriteAllBytes(hugeHere, hugeData.Reverse().ToArray());
        var env = new Dictionary<string, string>
        {
            ["CLIPBUBBLE_HOME"] = peerHome, ["CLIPBUBBLE_DOWNLOADS"] = Path.Combine(peerHome, "Downloads"), ["CLIPBUBBLE_DEVICE_NAME"] = "Mac",
            ["CLIPBUBBLE_CHILD"] = "serve", ["SEED_TEXT"] = "mac-old", ["SEED_FILE"] = big, ["SEED_BIG"] = huge, ["LIVE_TEXT"] = liveThere,
            ["SEED_DEVICE"] = "iphone-relayed|iPhone|ios",
        };

        using var db = new Db();
        db.Upsert(ClipKind.Text, "windows-old", null, "Notepad", "t:" + Sha("windows-old"));
        // Left from a link that dropped mid-download: the row is here, the bytes aren't.
        db.Insert(new Entry("f:" + Sha(big), Engine.Now - 1, "file", "big file.bin", "Files", "mac-device", new[] { new Blob("big file.bin", bigData.Length, "") }), "mac-device");
        db.Upsert(ClipKind.File, hugeHere, null, "Explorer", "f:" + Sha(hugeHere));
        db.SaveDevice("linux-relayed", "ROG", "linux");
        var e = new Engine("Windows test");
        var seen = new List<(string Hash, Transfer T)>();
        var applied = new List<string>();
        e.Transfers = t => { lock (seen) seen.AddRange(t.Select(p => (p.Key, p.Value))); };
        e.Apply = c => { lock (applied) applied.Add(c.Hash); };
        bool Applied(string h) { lock (applied) return applied.Contains(h); }

        var info = new ProcessStartInfo(swift) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var (k, v) in env) info.Environment[k] = v;
        using var p = Process.Start(info)!;
        var s = new Session(p.StandardOutput.BaseStream, p.StandardInput.BaseStream, e);
        e.Post(() => e.Begin(s));

        var macText = "t:" + Sha("mac-old");
        var fileHash = "f:" + Sha(big);
        Check(WaitFor(() => db.Exists(macText)), "the Mac's history arrives");
        Check(db.ByHash(macText)!.Origin.Length > 0, "marked as from the Mac");
        Check(db.DeviceNames().ContainsValue("Mac"), "the Mac's name is saved");
        Check(WaitFor(() => db.DeviceNames().GetValueOrDefault("iphone-relayed") == "iPhone"), "a device the Mac knows arrives with its name");
        Check(WaitFor(() => db.ByHash(fileHash) is { Remote: false }), "a download cut off by a dropped link finishes on the next one");
        var landed = db.ByHash(fileHash)!.Paths[0];
        Check(File.ReadAllBytes(landed).SequenceEqual(bigData), "the file's bytes match");
        Check(landed.EndsWith(Path.Combine("Downloads", "ClipBubble", "Mac", "big file.bin")), "lands in Downloads\\ClipBubble\\Mac: " + landed);
        Check(WaitFor(() => Applied("t:" + Sha(liveThere))), "a fresh copy on the Mac lands here");
        Check(!Applied(macText), "old history doesn't touch the clipboard");

        var appliedThere = Path.Combine(peerHome, "applied.txt");
        bool AppliedThere(string h) => File.Exists(appliedThere) && File.ReadAllText(appliedThere).Contains(h);
        var live = $"windows live {Engine.Now}";
        db.Upsert(ClipKind.Text, live, null, "Notepad", "t:" + Sha(live));
        Check(WaitFor(() => AppliedThere("t:" + Sha(live))), "a fresh copy here lands on the Mac");

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))).ToArray();
        var img = Path.Combine(Paths.Images, Sha(png) + ".png");
        File.WriteAllBytes(img, png);
        db.Upsert(ClipKind.Image, "", img, "Snipping Tool", "i:" + Sha(png));
        Check(WaitFor(() => AppliedThere("i:" + Sha(png))), "an image lands on the Mac");

        // Over 25 MB waits for Get, and resumes a partial file a dropped link left behind.
        var hh = "f:" + Sha(huge);
        Check(WaitFor(() => db.Exists(hh)), "the big file is listed");
        Thread.Sleep(500);
        Check(db.ByHash(hh)!.Remote, "a file over 25 MB doesn't download on its own");
        var partial = Path.Combine(Paths.Dir, "incoming", Sha(hh) + "-0");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        var head = new[] { (byte)(hugeData[0] ^ 0xFF) }.Concat(hugeData.Skip(1).Take((5 << 20) - 1)).ToArray();
        File.WriteAllBytes(partial, head);
        e.Get(hh);
        Check(WaitFor(() => db.ByHash(hh) is { Remote: false }, 30), "Get downloads it");
        var got = File.ReadAllBytes(db.ByHash(hh)!.Paths[0]);
        Check(got[0] == head[0] && got.Skip(1).SequenceEqual(hugeData.Skip(1)), "it resumed from the partial file");
        lock (seen) Check(seen.Any(x => x.Hash == hh && !x.T.Sending && x.T.Total == 26 << 20), "progress is reported");

        // Send to: this side asks the Mac to take a big file now.
        e.SendTo("f:" + Sha(hugeHere), File.ReadAllText(Path.Combine(peerHome, "device-id")).Trim());
        var there = Path.Combine(peerHome, "Downloads", "ClipBubble", "Windows test", "huge here.bin");
        Check(WaitFor(() => File.Exists(there) && new FileInfo(there).Length == hugeData.Length, 30), "Send to puts it on the Mac");
        lock (seen) Check(seen.Any(x => x.T.Sending), "sending shows progress");

        db.Delete("t:" + Sha(live));
        Thread.Sleep(700);
        e.Post(s.Close);
        Check(p.WaitForExit(10_000), "the Mac side ends with the session");

        var dump = new ProcessStartInfo(swift) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var (k, v) in env) dump.Environment[k] = v;
        dump.Environment["CLIPBUBBLE_CHILD"] = "dump";
        using var d = Process.Start(dump)!;
        var lines = d.StandardOutput.ReadToEnd().Split('\n');
        d.WaitForExit(10_000);
        Check(lines.Any(l => l.StartsWith("clip|t:" + Sha("windows-old") + "|")), "the Mac got this history");
        Check(lines.Contains("deleted|t:" + Sha(live)), "the delete reached the Mac");
        Check(lines.Any(l => l.StartsWith("clip|i:" + Sha(png) + "|") && l.EndsWith("|false|true")), "the image is stored on the Mac");
        Check(lines.Contains("device|linux-relayed|ROG"), "a device this side knows reaches the Mac with its name");
    }
}

/// Raw SQLite for fixtures, outside the app's Db.
sealed class RawDb : IDisposable
{
    readonly IntPtr h;
    public RawDb(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); sqlite3_open16(path, out h); }
    public void Exec(string sql) { if (sqlite3_exec(h, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("SQL failed"); }
    public void Dispose() => sqlite3_close_v2(h);
    [System.Runtime.InteropServices.DllImport("winsqlite3.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int sqlite3_open16(string path, out IntPtr db);
    [System.Runtime.InteropServices.DllImport("winsqlite3.dll")] static extern int sqlite3_close_v2(IntPtr db);
    [System.Runtime.InteropServices.DllImport("winsqlite3.dll")] static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, IntPtr err);
}
