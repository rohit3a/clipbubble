using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipBubble.Tests;

/// A tiny runner with no test framework, so the repo stays dependency-free. Exit code is the number of failures.
/// `--snapshots <dir>` renders the panel, peek and settings with the press-image data for a side-by-side with the Mac.
static class Program
{
    static readonly string Home = Path.Combine(Path.GetTempPath(), "clipbubble-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    [STAThread]
    static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("CLIPBUBBLE_HOME", Home);
        Directory.CreateDirectory(Home);
        try
        {
            if (args.Length == 2 && args[0] == "--snapshots") { Snapshots.Run(args[1]); return 0; }
            var failures = 0;
            foreach (var (name, test) in All())
            {
                try { Reset(); test(); Console.WriteLine("ok   " + name); }
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

    static void Reset()
    {
        using (var db = new Db()) db.DeleteAll();
        Sql.Exec(Paths.Db, "DELETE FROM forgotten");   // the deletes above are remembered for sync; each test starts clean
        foreach (var c in Prefs.Categories) Prefs.SetOn(c.Key, true);
        File.Delete(Path.Combine(Paths.Dir, "forgotten.json"));
        if (Directory.Exists(Mascots.Dir)) Directory.Delete(Mascots.Dir, true);
        Mascots.SetActive("blob");
    }

    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    static IEnumerable<(string, Action)> All() => new (string, Action)[]
    {
        ("updates compare versions as numbers", () =>
        {
            Check(Updater.IsNewer("1.10.0", "1.9.2"), "1.10.0 is newer than 1.9.2");
            Check(Updater.IsNewer("1.6.1", "1.6.0"), "1.6.1 is newer than 1.6.0");
            Check(!Updater.IsNewer("1.6.0", "1.6.0"), "the same version isn't an update");
            Check(!Updater.IsNewer("1.5.0", "1.6.0"), "an older release isn't an update");
        }),
        ("an update is the release's exe", () =>
        {
            var r = Updater.Parse(@"{""tag_name"": ""v1.7.0"", ""html_url"": ""https://github.com/rohit3a/clipbubble/releases/tag/v1.7.0"",
                ""assets"": [{""name"": ""ClipBubble-Windows.zip"", ""browser_download_url"": ""https://example.com/z""},
                             {""name"": ""ClipBubble.exe"", ""browser_download_url"": ""https://example.com/ClipBubble.exe""}]}");
            Check(r.Version == "1.7.0", "the tag's v is dropped");
            Check(r.Exe.EndsWith("/ClipBubble.exe"), "the exe, not the zip");
            var macOnly = @"{""tag_name"": ""v1.7.0"", ""html_url"": ""https://x"", ""assets"": [{""name"": ""ClipBubble.zip"", ""browser_download_url"": ""https://x/z""}]}";
            try { Updater.Parse(macOnly); Check(false, "a release without the exe isn't offered"); }
            catch (InvalidOperationException) { }
        }),
        ("schema matches the Mac app", () =>
        {
            using (new Db()) { }
            var sql = Sql.Query(Paths.Db, "SELECT sql FROM sqlite_master WHERE name IN ('clips','clips_created','forgotten') ORDER BY name");
            Check(Squash(sql[0]) == Squash(@"CREATE TABLE clips(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
              file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL,
              origin TEXT NOT NULL DEFAULT '', remote INTEGER NOT NULL DEFAULT 0)"), "clips table: " + sql[0]);
            Check(Squash(sql[1]) == Squash("CREATE INDEX clips_created ON clips(created)"), "index: " + sql[1]);
            Check(Squash(sql[2]) == Squash("CREATE TABLE forgotten(hash TEXT PRIMARY KEY, at REAL NOT NULL)"), "forgotten table: " + sql[2]);
            var devices = Sql.Query(Paths.Db, "SELECT sql FROM sqlite_master WHERE name = 'devices'");
            Check(Squash(devices[0]) == Squash("CREATE TABLE devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, os TEXT NOT NULL DEFAULT '')"), "devices: " + devices[0]);
            Check(Sql.Query(Paths.Db, "PRAGMA user_version")[0] == "1", "user_version 1");
            Check(Sql.Query(Paths.Db, "PRAGMA journal_mode")[0] == "wal", "WAL mode");
        }),

        ("upsert bumps a repeat and keeps Screenshot/Downloads sources", () =>
        {
            using var db = new Db();
            db.InsertIfMissing(ClipKind.File, @"C:\shots\a.png", "Screenshot", "f:a", DateTime.Now.AddHours(-2));
            db.Upsert(ClipKind.File, @"C:\shots\a.png", null, "Explorer", "f:a");
            var c = db.Clips().Single();
            Check(c.Source == "Screenshot", "source kept: " + c.Source);
            Check((DateTime.Now - c.Created).TotalSeconds < 5, "bumped to now");
            db.Upsert(ClipKind.Text, "hi", null, "Notepad", "t:hi");
            db.Upsert(ClipKind.Text, "hi", null, "Code", "t:hi");
            Check(db.Clips(kind: ClipKind.Text).Single().Source == "Code", "re-copy credits the new app");
            db.Upsert(ClipKind.Text, "hi", null, "Windows Terminal", "t:hi", keepSource: true);
            Check(db.Clips(kind: ClipKind.Text).Single().Source == "Code", "our own write keeps a known clip's source");
            db.Upsert(ClipKind.Text, "from an agent", null, "Windows Terminal", "t:agent", keepSource: true);
            Check(db.Clips(query: "agent").Single().Source == "Windows Terminal", "and gives a new clip the app in front");
        }),

        ("insertIfMissing never overwrites", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "x", null, "A", "t:x");
            db.InsertIfMissing(ClipKind.Text, "x", "B", "t:x", DateTime.Now.AddDays(-1));
            var c = db.Clips().Single();
            Check(c.Source == "A" && (DateTime.Now - c.Created).TotalMinutes < 1, "untouched");
        }),

        ("filters: image files count as images, source ignores case, query, since_hours", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "hello world", null, "Notepad", "t:1");
            db.InsertIfMissing(ClipKind.File, @"C:\pics\cat.JPG", "Screenshot", "f:2", DateTime.Now.AddHours(-3));
            db.InsertIfMissing(ClipKind.File, @"C:\docs\a.pdf", "Downloads", "f:3", DateTime.Now.AddHours(-1));
            Check(db.Clips(kind: ClipKind.Image).Select(c => c.Id).Count() == 1, "jpg file is an image");
            Check(db.Clips(source: "notepad").Count == 1, "NOCASE source");
            Check(db.Clips(query: "world").Count == 1, "query text");
            Check(db.Clips(query: "download").Count == 1, "query source");
            Check(db.Clips(sinceHours: 2).Count == 2, "since_hours");
        }),

        ("switching a category off hides it everywhere", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "secret-ish", null, "Notepad", "t:s");
            db.InsertIfMissing(ClipKind.File, @"C:\d\x.zip", "Downloads", "f:d", DateTime.Now);
            Prefs.SetOn("text", false);
            Check(db.Clips().All(c => c.Kind != ClipKind.Text), "text hidden");
            Prefs.SetOn("downloads", false);
            Check(db.Clips().Count == 0, "downloads hidden");
        }),

        ("prune drops old rows and only our own image copies", () =>
        {
            using var db = new Db();
            Directory.CreateDirectory(Paths.Images);
            var ours = Path.Combine(Paths.Images, "old.png");
            var theirs = Path.Combine(Home, "screenshot.png");
            File.WriteAllBytes(ours, new byte[] { 1 });
            File.WriteAllBytes(theirs, new byte[] { 1 });
            Sql.Exec(Paths.Db, $"INSERT INTO clips(kind,text,file,source,hash,created) VALUES('image','','{ours}','x','i:old',0)");
            db.InsertIfMissing(ClipKind.File, theirs, "Screenshot", "f:theirs", DateTime.Now.AddDays(-30));
            db.Prune();
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM clips")[0] == "0", "rows pruned");
            Check(!File.Exists(ours), "our image copy deleted");
            Check(File.Exists(theirs), "the user's screenshot kept");
        }),

        ("recorder: text, duplicate, and a paste-through rollback", () =>
        {
            var r = new Recorder(new Db());
            var t0 = DateTime.Now;
            Check(r.Record(Text("A"), "Notepad", t0), "A recorded");
            Check(!r.Record(Text("A"), "Notepad", t0.AddSeconds(1)), "same again ignored");
            Check(r.Record(Text("B"), "Wispr Flow", t0.AddSeconds(5)), "B recorded");
            Check(r.Record(Text("A"), "Notepad", t0.AddSeconds(6)), "A restored");
            using var db = new Db();
            var texts = db.Clips().Select(c => c.Text).ToList();
            Check(texts.SequenceEqual(new[] { "A" }), "B rolled back: " + string.Join(",", texts));
            Check((DateTime.Now - db.Clips().Single().Created).TotalSeconds < 30, "A not re-bumped");
        }),

        ("recorder: a restore after 2.5 s is a real copy, and an old item is never deleted", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "B", null, "Notepad", "t:" + Store.Sha("B"));
            var r = new Recorder(new Db());
            var t0 = DateTime.Now;
            r.Record(Text("A"), "Notepad", t0);
            r.Record(Text("B"), "Notepad", t0.AddSeconds(1));
            r.Record(Text("A"), "Notepad", t0.AddSeconds(2));
            Check(db.Clips().Any(c => c.Text == "B"), "existing B kept");
            r.Record(Text("C"), "Notepad", t0.AddSeconds(10));
            r.Record(Text("A"), "Notepad", t0.AddSeconds(20));
            Check(db.Clips().Any(c => c.Text == "C"), "C kept after 2.5 s");
        }),

        ("recorder: picking the previous item right after a copy keeps the copy, and isn't a new one", () =>
        {
            var r = new Recorder(new Db());
            var t0 = DateTime.Now;
            r.Record(Text("A"), "Notepad", t0);
            r.Record(Text("B"), "Edge", t0.AddSeconds(5));
            System.Threading.Thread.Sleep(20);   // history orders by time, in milliseconds
            var a = "t:" + Store.Sha("A");
            Recorder.Expect(a, a);
            Check(r.Record(Text("A"), "Notepad", t0.AddSeconds(6.5), ours: true), "the pick moves A up");
            Check(r.Quiet, "and doesn't count as a copy");
            using var db = new Db();
            Check(db.Clips().Select(c => c.Text).SequenceEqual(new[] { "A", "B" }), "B kept: " + string.Join(",", db.Clips().Select(c => c.Text)));
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM forgotten")[0] == "0", "and no delete goes out to linked devices");
            System.Threading.Thread.Sleep(20);
            Check(r.Record(Text("B"), "Edge", t0.AddSeconds(7)) && !r.Quiet, "copying B again right after counts");
            Check(db.Clips()[0].Text == "B", "and moves it up");
        }),

        ("recorder: a picked file from another machine moves up without counting as a copy", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.File, "/there/report.pdf", null, "Finder", "f:there");
            db.Upsert(ClipKind.Text, "newer", null, "Notepad", "t:newer");
            var r = new Recorder(new Db());
            var here = new Snapshot(new[] { "/here/report.pdf" }, null, null, null);
            System.Threading.Thread.Sleep(20);   // history orders by time, in milliseconds
            Recorder.Expect("f:" + Store.Sha("/here/report.pdf"), "f:there");
            Check(r.Record(here, "Explorer", DateTime.Now, ours: true), "the list changed");
            Check(r.Quiet, "but nothing new was copied");
            Check(db.Clips()[0].Hash == "f:there" && db.Clips().Count == 2, "the same row moved up");
        }),

        ("recorder: picking the last capture moves it above what synced since", () =>
        {
            using var db = new Db();
            var r = new Recorder(new Db());
            r.Record(Text("A"), "Notepad", DateTime.Now);
            System.Threading.Thread.Sleep(20);   // history orders by time, in milliseconds
            db.Upsert(ClipKind.Text, "synced", null, "Edge", "t:synced");
            System.Threading.Thread.Sleep(20);
            var a = "t:" + Store.Sha("A");
            Recorder.Expect(a, a);
            Check(r.Record(Text("A"), "Notepad", DateTime.Now, ours: true) && r.Quiet, "the list changed, quietly");
            Check(db.Clips()[0].Text == "A", "A moved up");
        }),

        ("recorder: a paste-through right after a synced copy still rolls back", () =>
        {
            var r = new Recorder(new Db());
            var t0 = DateTime.Now;
            r.Record(Text("A"), "Notepad", t0);
            r.Record(Text("B"), "Edge", t0.AddSeconds(5));
            using var db = new Db();
            var c = "t:" + Store.Sha("C");
            db.Upsert(ClipKind.Text, "C", null, "Edge", c);
            Recorder.Expect(c, null);
            Check(!r.Record(Text("C"), "Edge", t0.AddSeconds(20), ours: true), "a synced copy landing is already in history");
            r.Record(Text("tool text"), "Wispr Flow", t0.AddSeconds(25));
            r.Record(Text("C"), "Edge", t0.AddSeconds(26));
            Check(!db.Clips().Any(x => x.Text == "tool text"), "the tool's text is gone");
        }),

        ("recorder: only picks and undone paste-throughs are quiet", () =>
        {
            var r = new Recorder(new Db());
            var t0 = DateTime.Now;
            Check(r.Record(Text("agent"), "Code", t0, ours: true) && !r.Quiet, "ClipBubble's own clipboard write counts when nothing expected it: an agent's or a screenshot's");
            Check(r.Record(Text("B"), "Wispr Flow", t0.AddSeconds(5)) && !r.Quiet, "a normal copy counts");
            Check(r.Record(Text("agent"), "Code", t0.AddSeconds(6)), "the restore changes the list");
            Check(r.Quiet, "but undoing a paste-through isn't a copy");
        }),

        ("recorder: files first, URL+image is an image, text+image is text, blank is nothing", () =>
        {
            var png = TinyPng();
            var r = new Recorder(new Db());
            r.Record(new Snapshot(new[] { @"C:\a.txt", @"C:\b.txt" }, "ignored", png, null), "Explorer", DateTime.Now);
            r.Record(new Snapshot(Array.Empty<string>(), "https://example.com/cat.png", png, null), "Edge", DateTime.Now.AddSeconds(10));
            r.Record(new Snapshot(Array.Empty<string>(), "a caption", TinyPng(2), null), "Word", DateTime.Now.AddSeconds(20));
            Check(!r.Record(new Snapshot(Array.Empty<string>(), "   ", null, null), "x", DateTime.Now.AddSeconds(30)), "blank ignored");
            using var db = new Db();
            var clips = db.Clips();
            Check(clips.Any(c => c.Kind == ClipKind.File && c.Paths.Length == 2), "files");
            var img = clips.Single(c => c.Kind == ClipKind.Image);
            Check(img.File != null && File.Exists(img.File) && Path.GetDirectoryName(img.File) == Paths.Images, "image saved as PNG");
            Check(clips.Any(c => c.Kind == ClipKind.Text && c.Text == "a caption"), "text wins over image");
        }),

        ("recorder: a 24-bit clipboard bitmap (DIB) becomes a PNG", () =>
        {
            var r = new Recorder(new Db());
            Check(r.Record(new Snapshot(Array.Empty<string>(), null, null, Dib24(3, 2)), "Paint", DateTime.Now), "recorded");
            using var db = new Db();
            var img = db.Clips().Single();
            var frame = BitmapDecoder.Create(new Uri(img.File!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.PixelWidth == 3 && frame.PixelHeight == 2, $"size {frame.PixelWidth}x{frame.PixelHeight}");
        }),

        ("an image copied by an app that only lists Bitmap is still read", () =>
        {
            var dib = Dib24(3, 2);
            var snap = Capture.Read(new FakeClipboard(new[] { "Bitmap" }, f => f == DataFormats.Dib ? new MemoryStream(dib) : null));
            Check(snap?.Dib != null && snap.Dib.Length == dib.Length, "DIB requested even though only Bitmap was listed");
        }),

        ("text from an app that only lists ANSI text is still read", () =>
        {
            var snap = Capture.Read(new FakeClipboard(new[] { DataFormats.Text }, f => f == DataFormats.UnicodeText ? "plain old text" : null));
            Check(snap?.Text == "plain old text", "Unicode text requested even though only Text was listed");
        }),

        ("a delete that fails on a locked database keeps the clip's image", () =>
        {
            var r = new Recorder(new Db());
            r.Record(new Snapshot(Array.Empty<string>(), null, TinyPng(7), null), "Paint", DateTime.Now);
            using var db = new Db();
            var clip = db.Clips().Single();
            using (Sql.Hold(Paths.Db, "BEGIN IMMEDIATE"))
            {
                try { db.Delete(clip.Id); } catch (InvalidOperationException) { }
            }
            Check(db.Clips().Count == 1 && File.Exists(clip.File!), "row and its image are both still there");
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM forgotten")[0] == "0", "and nothing half-remembered");
        }),

        ("app names that are really file names read like names", () =>
        {
            Check(Apps.Readable("SnippingTool.exe") == "Snipping Tool", Apps.Readable("SnippingTool.exe"));
            Check(Apps.Readable("Microsoft.Photos.exe") == "Photos", Apps.Readable("Microsoft.Photos.exe"));
            Check(Apps.Readable("msedge.exe") == "msedge", "lowercase names stay");
            Check(Apps.Readable("Google Chrome") == "Google Chrome", "real descriptions are untouched");
        }),

        ("secrets marked by password managers are never read", () =>
        {
            Check(Capture.Read(new FakeClipboard(new[] { DataFormats.UnicodeText, Board.ExcludeFormat }, f => f == DataFormats.UnicodeText ? "pw" : null)) == null, "exclude marker");
            Check(Capture.Read(new FakeClipboard(new[] { DataFormats.UnicodeText, "CanIncludeInClipboardHistory" }, f => f == DataFormats.UnicodeText ? "pw" : new MemoryStream(new byte[4]))) == null, "history opt-out");
            Check(Capture.Read(new FakeClipboard(new[] { DataFormats.UnicodeText }, f => f == DataFormats.UnicodeText ? "hello" : null))?.Text == "hello", "plain text still read");
        }),

        ("MCP history keeps its 1 to 100 limit for any number", () =>
        {
            using var db = new Db();
            for (var i = 0; i < 3; i++) db.Upsert(ClipKind.Text, "item " + i, null, "Notepad", "t:limit" + i);
            string History(object limit) => (string)((Dictionary<string, object>)((object[])ClipBubble.Mcp.Call(db, "clipboard_history", new Dictionary<string, object?> { ["limit"] = limit })["content"])[0])["text"];
            Check(History(-1).Split(new[] { "\n\n" }, StringSplitOptions.None).Length == 1, "negative limit gives one item, not everything");
            Check(History(2).Split(new[] { "\n\n" }, StringSplitOptions.None).Length == 2, "normal limit");
        }),

        ("recorder: switched-off kinds aren't saved", () =>
        {
            Prefs.SetOn("text", false);
            Check(!new Recorder(new Db()).Record(Text("nope"), "Notepad", DateTime.Now), "skipped");
            using var db = new Db();
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM clips")[0] == "0", "nothing stored");
        }),

        ("a failed database write throws, and the copy isn't treated as saved", () =>
        {
            using (new Db()) { }
            Sql.Exec(Paths.Db, "CREATE TRIGGER refuse BEFORE INSERT ON clips BEGIN SELECT RAISE(ABORT, 'disk full'); END");
            var r = new Recorder(new Db());
            var threw = false;
            try { r.Record(Text("important"), "Notepad", DateTime.Now); } catch (InvalidOperationException) { threw = true; }
            Sql.Exec(Paths.Db, "DROP TRIGGER refuse");
            Check(threw, "the write error surfaced");
            Check(r.Record(Text("important"), "Notepad", DateTime.Now.AddSeconds(1)), "the same text is saved on the next try, not skipped as a duplicate");
            using var db = new Db();
            Check(db.Clips().Single().Text == "important", "stored");
        }),

        ("settings save while another process is reading them, and an unreadable file keeps the last switches", () =>
        {
            Prefs.SetOn("text", false);
            var file = Path.Combine(Paths.Dir, "prefs.json");
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                Prefs.SetOn("images", false);
            Prefs.Reload();
            Check(!Prefs.On("text") && !Prefs.On("images"), "both saved");
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Prefs.Reload();
            Check(!Prefs.On("text"), "a locked file doesn't reset hidden categories to visible");
        }),

        ("mascot files keep the Mac JSON shape; remove deletes only its own files", () =>
        {
            var fox = Mascots.Add("Fox", "image", null, new() { ["idle"] = Svg });
            var fox2 = Mascots.Add("Fox", "image", null, new() { ["idle"] = Svg });
            Check(fox.Id == "fox" && fox2.Id == "fox-2", $"ids {fox.Id} {fox2.Id}");
            var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Mascots.Dir, "fox.json")));
            Check(json.Keys.OrderBy(k => k).SequenceEqual(new[] { "id", "idle", "kind", "name" }), "keys " + string.Join(",", json.Keys));
            Check((string)json["idle"] == "fox-idle.svg", "idle file");
            Mascots.SetActive("fox");
            Check(Mascots.Remove("fox"), "removed");
            Check(File.Exists(Path.Combine(Mascots.Dir, "fox-2-idle.svg")) && File.Exists(Path.Combine(Mascots.Dir, "fox-2.json")), "fox-2 untouched");
            Check(Mascots.ActiveId() == "blob", "active falls back to blob");
            Check(!Mascots.Remove("cat"), "built-ins stay");
        }),

        ("removing a mascot never deletes files another mascot owns", () =>
        {
            Mascots.Add("Victim", "blob", "#FF6AD5", new());
            Mascots.Add("Sneaky", "emoji", null, new() { ["idle"] = "victim.json" });
            Check(Mascots.Remove("sneaky"), "removed");
            Check(File.Exists(Path.Combine(Mascots.Dir, "victim.json")), "victim.json survives");
        }),

        ("a hidden category can't be fetched by id either", () =>
        {
            long id;
            using (var db = new Db())
            {
                db.Upsert(ClipKind.Text, "private note", null, "Notepad", "t:private");
                id = db.Clips().Single().Id;
            }
            Prefs.SetOn("text", false);
            using var db2 = new Db();
            Check(db2.Clip(id) == null, "hidden from lookup by id");
            Prefs.SetOn("text", true);
            Check(db2.Clip(id)?.Text == "private note", "visible again when switched back on");
        }),

        ("a screenshot saved into a folder that didn't exist yet is still caught", () =>
        {
            var folder = Path.Combine(Paths.Dir, "later-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var seen = new List<string>();
            using var watcher = new FolderWatcher(folder, 120, _ => true, (path, _) => seen.Add(path));
            Directory.CreateDirectory(folder);
            var shot = Path.Combine(folder, "Screenshot 1.png");
            File.WriteAllBytes(shot, TinyPng());
            Snapshots.Pump(6);   // the watcher re-checks for a missing folder every 5 s
            Check(seen.Contains(shot), "first screenshot reported: " + string.Join(",", seen));
        }),

        ("showing a thumbnail never leaves the user's file locked", () =>
        {
            var shot = Path.Combine(Paths.Dir, "locked-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
            File.WriteAllBytes(shot, TinyPng());
            BitmapSource? thumb = null;
            Thumbs.Load(shot, 64, b => thumb = b);
            Snapshots.Pump(1);
            Check(thumb != null && Thumbs.SizeOf(shot) is { Width: 4 }, "thumbnail and size loaded");
            File.Delete(shot);
            Check(!File.Exists(shot), "file could be deleted right away");
        }),

        ("a big animated GIF mascot is decoded at the size it's shown", () =>
        {
            var gif = Path.Combine(Paths.Dir, "big.gif");
            var enc = new GifBitmapEncoder();
            foreach (var shade in new byte[] { 40, 160 })
            {
                var px = new byte[480 * 480 * 4];
                for (var i = 0; i < px.Length; i += 4) { px[i] = shade; px[i + 1] = 90; px[i + 2] = 200; px[i + 3] = 255; }
                enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 480, 96, 96, PixelFormats.Bgra32, null, px, 480 * 4)));
            }
            using (var f = File.Create(gif)) enc.Save(f);
            var image = new System.Windows.Controls.Image();
            Check(GifPlayer.Create(gif, image) != null, "decoded");
            var first = (BitmapSource)image.Source;
            Check(first.PixelWidth <= 102 && first.PixelHeight <= 102, $"frame is {first.PixelWidth}x{first.PixelHeight}; expected at most 102 px, not the GIF's full 480");
        }),

        ("MCP times read the same in every region", () =>
        {
            using (var db = new Db()) db.Upsert(ClipKind.Text, "hello", null, "Notepad", "t:hello");
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fi-FI");   // writes times as 14.30
            try
            {
                using var db = new Db();
                var text = (string)((Dictionary<string, object>)((object[])ClipBubble.Mcp.Call(db, "clipboard_history", new Dictionary<string, object?>())["content"])[0])["text"];
                Check(System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}"), text);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
        }),

        ("a screenshot that's both copied and saved shows up once, in either order", () =>
        {
            var shot = Path.Combine(Paths.Dir, "Screenshot 1.png");
            File.WriteAllBytes(shot, TinyPng(3));
            var reencoded = Reencode(TinyPng(3));
            using var db = new Db();
            var r = new Recorder(new Db());
            r.AddFile(shot, "Screenshot", DateTime.Now);
            Check(!r.Record(new Snapshot(Array.Empty<string>(), null, reencoded, null), "Snipping Tool", DateTime.Now), "copy after the file is skipped");
            Check(db.Clips().Count == 1, "file first: one row");

            db.DeleteAll();
            Sql.Exec(Paths.Db, "DELETE FROM forgotten");   // Clear all would keep the screenshot out from now on
            r = new Recorder(new Db());
            r.Record(new Snapshot(Array.Empty<string>(), null, reencoded, null), "Snipping Tool", DateTime.Now);
            r.AddFile(shot, "Screenshot", DateTime.Now);
            Check(db.Clips().Single().Source == "Screenshot", "copy first: the file's row replaces it");

            r.Record(new Snapshot(Array.Empty<string>(), null, TinyPng(5), null), "Paint", DateTime.Now);
            r.AddFile(shot, "Screenshot", DateTime.Now);
            Check(db.Clips().Count == 2, "a different picture copied at the same time stays");
        }),

        ("mascot validation matches the Mac messages", () =>
        {
            string Err(Action a) { try { a(); return ""; } catch (MascotException e) { return e.Message; } }
            Check(Err(() => Mascots.Add("x", "robot", null, new())) == "kind must be blob, emoji or image", "kind");
            Check(Err(() => Mascots.Add("x", "blob", "pink", new())) == "blob needs color as a hex like #FF6AD5", "color");
            Check(Err(() => Mascots.Add("x", "emoji", null, new())) == "emoji needs an idle value", "idle");
            Check(Err(() => Mascots.Add("x", "image", null, new() { ["idle"] = "<svg nonsense" })).StartsWith("couldn't read the idle image"), "bad svg");
        }),

        ("color emoji and SVG render with color", () =>
        {
            Check(Colorful(Glyphs.Emoji("🐸", 30)), "frog is green, not monochrome");
            Check(Colorful(Glyphs.Emoji("👩🏽‍🚀", 30)), "ZWJ sequence");
            var svg = Path.Combine(Home, "t.svg");
            File.WriteAllText(svg, Svg);
            Check(Colorful(Glyphs.Load(svg, 34)), "svg");
        }),

        ("spring easing settles at the target and overshoots when underdamped", () =>
        {
            var bouncy = new SpringEase(0.2, 0.45);
            Check(Math.Abs(bouncy.Ease(1) - 1) < 0.01, "settles");
            Check(Enumerable.Range(0, 100).Any(i => bouncy.Ease(i / 100.0) > 1.05), "overshoots");
            var stiff = new SpringEase(0.2, 1);
            Check(Enumerable.Range(0, 100).All(i => stiff.Ease(i / 100.0) <= 1.0001), "critically damped never overshoots");
        }),

        ("a picture that fails to decode in an unexpected way still finishes loading", () =>
        {
            var broken = Path.Combine(Paths.Dir, "broken-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".webp");
            File.WriteAllBytes(broken, BrokenWebp());
            var calls = 0;
            Thumbs.Load(broken, 64, _ => calls++);
            Snapshots.Pump(1);
            Thumbs.Load(broken, 64, _ => calls++);
            Snapshots.Pump(1);
            Check(calls == 2, $"{calls} of 2 callbacks ran; a stuck decode leaves the row blank until a restart");
            Thumbs.Load("C:\\bad<name>.png", 64, _ => calls++);   // a path Windows rejects outright
            Snapshots.Pump(1);
            Check(calls == 3, "a path that can't even be looked at still finishes");
        }),

        ("a broken WebP doesn't break copies, agents or mascots", () =>
        {
            var broken = Path.Combine(Paths.Dir, "Screenshot broken.webp");
            File.WriteAllBytes(broken, BrokenWebp());
            using var db = new Db();
            var r = new Recorder(db);
            r.AddFile(broken, "Screenshot", DateTime.Now);
            Check(r.Record(new Snapshot(Array.Empty<string>(), null, TinyPng(9), null), "Paint", DateTime.Now), "a picture copied right after is still saved");
            var id = db.Clips(kind: ClipKind.Image).Single(c => c.Source == "Screenshot").Id;
            var got = ClipBubble.Mcp.Call(db, "clipboard_get", new Dictionary<string, object?> { ["id"] = id });
            Check(((System.Collections.IList)got["content"]).Count == 1, "clipboard_get returns the item without an image");
            string Err(Action a) { try { a(); return ""; } catch (MascotException e) { return e.Message; } }
            Check(Err(() => Mascots.Add("Broken", "image", null, new() { ["idle"] = broken })).StartsWith("couldn't read the idle image"), "mascot_add explains");
            Check(!File.Exists(Path.Combine(Mascots.Dir, "broken-idle.webp")), "and leaves no copy of the file behind");
        }),

        ("an image whose file is gone is never pasted as whatever was copied before", () =>
        {
            var gone = new Clip(1, ClipKind.Image, "", Path.Combine(Paths.Images, "gone.png"), "Paint", DateTime.Now);
            Check(Board.Write(gone) == "Its image file is gone or can't be read.", "missing file: nothing written, so no paste");
            var junk = Path.Combine(Paths.Images, "junk.png");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });
            Check(Board.Write(gone with { File = junk }) != null, "unreadable file: same");
            var broken = Path.Combine(Paths.Dir, "broken.webp");
            File.WriteAllBytes(broken, BrokenWebp());
            Check(Board.Write(gone with { File = broken }) != null, "broken WebP: same");
            Check(Board.Write(new Clip(2, ClipKind.File, Path.Combine(Paths.Dir, "gone.pdf"), null, "Downloads", DateTime.Now)) == "Its files are gone.", "a file clip whose file is gone: same");
            File.Delete(broken);   // throws if the failed read left the user's file locked
            using var db = new Db();
            Sql.Exec(Paths.Db, $"INSERT INTO clips(kind,text,file,source,hash,created) VALUES('image','','{gone.File}','Paint','i:gone',{DateTimeOffset.UtcNow.ToUnixTimeSeconds()})");
            var reply = ClipBubble.Mcp.Call(db, "clipboard_set", new Dictionary<string, object?> { ["id"] = db.Clips().Single().Id });
            Check(reply["isError"] is true, "clipboard_set tells the agent it didn't copy");
        }),

        ("auto-paste waits for the app, never pastes after the clipboard changed, and says why it didn't paste", () =>
        {
            static string? Paste(Func<bool> ready, Func<bool> stillOurs, Func<bool> send, double timeout = 1.5) =>
                System.Threading.Tasks.Task.Run(() => Win.PasteWhenReady(ready, stillOurs, send, timeout)).Result;
            var polls = 0;
            var sent = 0;
            Check(Paste(() => ++polls >= 5, () => true, () => { sent++; return true; }) == null && sent == 1, $"pasted once the app was ready (sent {sent})");
            Check(polls == 5, $"checked {polls} times before pasting");
            sent = 0;
            var watch = Stopwatch.StartNew();
            Check(Paste(() => false, () => true, () => { sent++; return true; }, timeout: 0.2) is { } late && late.Contains("Ctrl+V") && sent == 0, "app never came back: no paste, and press Ctrl+V");
            Check(watch.Elapsed.TotalSeconds < 1, $"gave up after {watch.Elapsed.TotalSeconds:0.00} s");
            Check(Paste(() => true, () => false, () => { sent++; return true; }) is { } replaced && replaced.StartsWith("Another app changed the clipboard") && sent == 0, "clipboard replaced meanwhile: no paste, and says so");
            Check(Paste(() => false, () => false, () => true, timeout: 0.2)?.StartsWith("Another app changed the clipboard") == true, "app never came back and the clipboard changed: doesn't say to press Ctrl+V");
            Check(Paste(() => true, () => true, () => false)?.StartsWith("Windows blocked the keystrokes") == true, "keystrokes refused: says so");
        }),

        ("deleting a clip or clearing history never waits on the database", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "keep", null, "Notepad", "t:keep");
            db.Upsert(ClipKind.Text, "drop", null, "Notepad", "t:drop");
            var model = new Model();
            model.Refresh();
            var held = Sql.Hold(Paths.Db, "BEGIN IMMEDIATE");   // another app in the middle of a write
            var watch = Stopwatch.StartNew();
            model.Delete(model.Clips.Single(c => c.Text == "drop"));
            Check(watch.ElapsedMilliseconds < 200, $"delete returned in {watch.ElapsedMilliseconds} ms");
            Check(model.Clips.Select(c => c.Text).SequenceEqual(new[] { "keep" }), "gone from the list at once");
            held.Dispose();
            Snapshots.Pump(0.5);
            Check(db.Clips().Select(c => c.Text).SequenceEqual(new[] { "keep" }), "deleted for real");
            Check(model.Recent.All(c => c.Text != "drop"), "and from the recent three");

            held = Sql.Hold(Paths.Db, "BEGIN IMMEDIATE");
            watch.Restart();
            model.ClearAll();
            Check(watch.ElapsedMilliseconds < 200, $"clear all returned in {watch.ElapsedMilliseconds} ms");
            held.Dispose();
            Snapshots.Pump(0.5);
            Check(model.Clips.Count == 0 && db.Clips().Count == 0, "everything cleared");
        }),

        ("a delete that fails shows the clip again", () =>
        {
            using (var db = new Db()) db.Upsert(ClipKind.Text, "stuck", null, "Notepad", "t:stuck");
            var model = new Model();
            string? error = null, detail = null;
            model.ShowError = (title, why) => (error, detail) = (title, why);
            model.Refresh();
            using (Sql.Hold(Paths.Db, "BEGIN IMMEDIATE"))
            {
                model.Delete(model.Clips.Single());
                Check(model.Clips.Count == 0, "hidden while deleting");
                Snapshots.Pump(3.5);   // past the 3 s busy timeout
            }
            Check(model.Clips.Count == 1 && model.Recent.Count == 1, "back in the list, because it's still stored");
            Check(error == "Couldn't delete that", "and says so: " + error);
            Check(detail?.StartsWith("Another app is using") == true, "a locked database is put in plain words: " + detail);
        }),

        ("Settings starts with Hide bubble until needed: off, saved, and with its shortcut only while on", () =>
        {
            var settings = new SettingsView(new Model(), TestApp.Value);
            List<string> Words() => Descendants(settings).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text).ToList();
            Check(!App.HideBubble, "off by default");
            Check(Words().IndexOf("Hide bubble until needed") is >= 0 and var at && at < Words().IndexOf("Copied text"), "first in Settings");
            Check(!Words().Contains("Show or hide bubble"), "no shortcut row while it's off");
            Click(Switches(settings).First());
            Check(App.HideBubble, "switched on");
            Prefs.Reload();
            Check(App.HideBubble, "still on when read back from disk, as after a restart");
            Check(Words().Contains("Show or hide bubble") && Words().Contains("Ctrl+Alt+B"), "the shortcut row appears");
            Click(Switches(settings).First());
            Check(!App.HideBubble, "switched off");
        }),

        ("the bubble's shortcut defaults to Ctrl+Alt+B and comes back as saved", () =>
        {
            App.GlobalKey Key() => new(2, "bubbleHotkey", System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.B);
            Check(Key().Text == "Ctrl+Alt+B", Key().Text);
            Prefs.Set("bubbleHotkey", "Ctrl+Shift+K");
            Check(Key().Text == "Ctrl+Shift+K", Key().Text);
            Prefs.Set("bubbleHotkey", "nonsense");
            Check(Key().Text == "Ctrl+Alt+B", "an unreadable one falls back: " + Key().Text);
            Prefs.Set("bubbleHotkey", null);
        }),

        ("a setting that can't be saved stays as it was and says why", () =>
        {
            var model = new Model();
            string? error = null;
            model.ShowError = (title, _) => error = title;
            var settings = new SettingsView(model, TestApp.Value);
            var textSwitch = Switches(settings).ElementAt(1);   // after Hide bubble until needed
            using (new FileStream(Path.Combine(Paths.Dir, "prefs.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Click(textSwitch);
            Check(error == "Couldn't save that setting", "said why: " + error);
            Check(Prefs.On("text") && textSwitch.Background == UI.AccentBrush, "the setting and its switch are still on");
            Check(Directory.GetFiles(Paths.Dir, "*.tmp").Length == 0, "no temp file left behind");
            Click(textSwitch);
            Check(!Prefs.On("text") && textSwitch.Background != UI.AccentBrush, "works once the file is free");
        }),

        ("a row picked with the keyboard keeps its highlight when the mouse leaves it", () =>
        {
            using (var db = new Db()) { db.Upsert(ClipKind.Text, "one", null, "Notepad", "t:one"); db.Upsert(ClipKind.Text, "two", null, "Notepad", "t:two"); }
            var model = new Model();
            model.Refresh();
            var second = model.Clips[1];
            var row = new Row(second, 1, isSelected: false, model);
            var host = new Window { Content = row, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight };
            host.Show();
            try
            {
                model.Selection = second.Id;
                var selected = ((SolidColorBrush)row.Background).Color;
                row.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                Check(row.Background is SolidColorBrush b && b.Color == selected && selected.A > 0, "still highlighted");
            }
            finally { host.Close(); }
        }),

        ("copying something again right after deleting it saves it again", () =>
        {
            using var db = new Db();
            var r = new Recorder(db);
            r.Record(Text("again"), "Notepad", DateTime.Now);
            db.Delete(db.Clips().Single().Id);
            Check(r.Record(Text("again"), "Notepad", DateTime.Now.AddSeconds(5)), "recorded");
            Check(db.Clips().Single().Text == "again", "back in history");
        }),

        ("text with a NUL character is stored whole", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "before\0after", null, "Notepad", "t:nul");
            Check(db.Clips().Single().Text == "before\0after", "read back: " + db.Clips().Single().Text.Replace("\0", "\\0"));
        }),

        ("clearing history doesn't stop at a read-only image", () =>
        {
            using var db = new Db();
            var r = new Recorder(db);
            r.Record(new Snapshot(Array.Empty<string>(), null, TinyPng(11), null), "Paint", DateTime.Now);
            r.Record(new Snapshot(Array.Empty<string>(), null, TinyPng(12), null), "Paint", DateTime.Now.AddSeconds(5));
            var files = db.Clips().Select(c => c.File!).ToList();
            File.SetAttributes(files[0], FileAttributes.ReadOnly);
            try
            {
                db.DeleteAll();
                Check(db.Clips().Count == 0, "rows gone");
                Check(!File.Exists(files[1]), "the other image was still deleted");
            }
            finally { File.SetAttributes(files[0], FileAttributes.Normal); }
        }),

        ("a mascot named only with symbols gets a readable id", () =>
        {
            Check(Mascots.Add("!!!", "emoji", null, new() { ["idle"] = "🐸" }).Id == "mascot", "first");
            Check(Mascots.Add("???", "emoji", null, new() { ["idle"] = "🐸" }).Id == "mascot-2", "second, not \"-2\"");
        }),

        ("a big picture mascot is decoded at the size it's shown", () =>
        {
            var big = Path.Combine(Paths.Dir, "big.png");
            var px = new byte[2000 * 1000 * 4];
            for (var i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 90; px[i + 2] = 40; px[i + 3] = 255; }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2000, 1000, 96, 96, PixelFormats.Bgra32, null, px, 2000 * 4)));
            using (var f = File.Create(big)) enc.Save(f);
            var img = Glyphs.Load(big, 34);
            Check(img != null && img.PixelWidth <= 102 && img.PixelHeight <= 51, $"decoded at {img?.PixelWidth}x{img?.PixelHeight} to show at 34 px");
        }),

        ("removed screenshots and downloads stay out of history, like on the Mac", () =>
        {
            var shot = Path.Combine(Paths.Dir, "Screenshot 2.png");
            var download = Path.Combine(Paths.Dir, "report.pdf");
            var copied = Path.Combine(Paths.Dir, "notes.txt");
            File.WriteAllBytes(shot, TinyPng());
            File.WriteAllText(download, "%PDF");
            File.WriteAllText(copied, "x");
            using var db = new Db();
            // Like the folder import: each file comes in with when it was saved.
            void Import()
            {
                db.InsertIfMissing(ClipKind.File, shot, "Screenshot", "f:" + Store.Sha(shot), File.GetCreationTime(shot));
                db.InsertIfMissing(ClipKind.File, download, "Downloads", "f:" + Store.Sha(download), File.GetCreationTime(download));
            }
            Import();
            db.Upsert(ClipKind.File, copied, null, "File Explorer", "f:" + Store.Sha(copied));
            var model = new Model();
            model.Refresh();
            model.Delete(model.Clips.Single(c => c.Source == "Screenshot"));
            model.Delete(model.Clips.Single(c => c.Source == "File Explorer"));
            Snapshots.Pump(0.5);
            Import();
            var sources = db.Clips().Select(c => c.Source).ToList();
            Check(sources.SequenceEqual(new[] { "Downloads" }), "the removed screenshot isn't imported again: " + string.Join(",", sources));
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM forgotten")[0] == "2", "every delete is remembered, so linked machines hear about it");
            var later = Path.Combine(Paths.Dir, "Screenshot 3.png");
            File.WriteAllBytes(later, TinyPng());
            Sql.Exec(Paths.Db, $"INSERT INTO forgotten(hash, at) VALUES('f:{Store.Sha(later)}', {DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60})");
            db.InsertIfMissing(ClipKind.File, later, "Screenshot", "f:" + Store.Sha(later), File.GetCreationTime(later));
            Check(db.Exists("f:" + Store.Sha(later)), "a new file saved at a removed path later still comes in");
            db.Delete("f:" + Store.Sha(later));
            model.ClearAll();
            Snapshots.Pump(0.5);
            Import();
            Check(db.Clips().Count == 0, "after Clear all, neither comes back");
            Sql.Exec(Paths.Db, $"UPDATE forgotten SET at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 9 * 24 * 3600}");
            db.Prune();
            Check(Sql.Query(Paths.Db, "SELECT count(*) FROM forgotten")[0] == "0", "reminders older than the window plus a day are dropped");
        }),

        ("a folder that briefly can't be read doesn't make old files look new", () =>
        {
            var folder = Path.Combine(Paths.Dir, "watched-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "old.pdf"), "x");
            var seen = new List<string>();
            using var watcher = new FolderWatcher(folder, null, _ => true, (path, _) => seen.Add(Path.GetFileName(path)));
            var dir = new DirectoryInfo(folder);
            var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
            var acl = dir.GetAccessControl();
            acl.AddAccessRule(deny);
            dir.SetAccessControl(acl);
            try
            {
                File.WriteAllText(Path.Combine(folder, "during.pdf"), "x");   // noticed, but the folder can't be listed
                Snapshots.Pump(1);
            }
            finally
            {
                acl.RemoveAccessRule(deny);
                dir.SetAccessControl(acl);
            }
            File.WriteAllText(Path.Combine(folder, "after.pdf"), "x");
            Snapshots.Pump(1);
            Check(!seen.Contains("old.pdf"), "old file not reported as new: " + string.Join(",", seen));
            Check(seen.Contains("during.pdf") && seen.Contains("after.pdf"), "new files still reported: " + string.Join(",", seen));
        }),

        ("a thumbnail that failed while its file was still saving loads once it's saved", () =>
        {
            var shot = Path.Combine(Paths.Dir, "saving-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
            File.WriteAllBytes(shot, TinyPng().Take(20).ToArray());
            BitmapSource? first = null, second = null;
            Thumbs.Load(shot, 64, b => first = b);
            Snapshots.Pump(0.5);
            File.WriteAllBytes(shot, TinyPng());
            Thumbs.Load(shot, 64, b => second = b);
            Snapshots.Pump(0.5);
            Check(first == null && second != null, "loaded on the second try");
        }),

        ("the home folder shortens to ~ only for paths inside it", () =>
        {
            Check(UI.Tilde(@"C:\Users\Ann\notes.txt", @"C:\Users\Ann") == @"~\notes.txt", "inside");
            Check(UI.Tilde(@"C:\Users\Anna\notes.txt", @"C:\Users\Ann") == @"C:\Users\Anna\notes.txt", "a neighbor that starts the same");
            Check(UI.Tilde(@"C:\Users\Ann", @"C:\Users\Ann") == "~", "the folder itself");
        }),

        ("a screenshot still being saved is compared once it's done", () =>
        {
            var shot = Path.Combine(Paths.Dir, "Screenshot 3.png");
            var png = TinyPng(4);
            using var db = new Db();
            var r = new Recorder(db);
            r.Record(new Snapshot(Array.Empty<string>(), null, Reencode(png), null), "Snipping Tool", DateTime.Now);
            var saving = new FileStream(shot, FileMode.Create, FileAccess.Write, FileShare.Read);
            var add = System.Threading.Tasks.Task.Run(() => r.AddFile(shot, "Screenshot", DateTime.Now));
            Thread.Sleep(400);
            saving.Write(png, 0, png.Length);
            saving.Dispose();
            Check(add.Wait(5000), "finished");
            Check(db.Clips().Count == 1 && db.Clips()[0].Source == "Screenshot", "one row: " + string.Join(", ", db.Clips().Select(c => c.Source)));
        }),

        ("a new search shows its results from the top; a delete keeps the list where it was", () =>
        {
            using (var db = new Db())
                for (var i = 0; i < 60; i++) db.InsertIfMissing(ClipKind.Text, "item " + i, "Notepad", "t:item" + i, DateTime.Now.AddMinutes(-i));
            var model = new Model();
            model.Refresh();
            var panel = new PanelView(model, () => new SettingsView(model, TestApp.Value));
            var host = new Window { Content = panel, Width = 420, Height = 470, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            host.Show();
            Snapshots.Pump(0.3);
            try
            {
                var list = Visuals(panel).OfType<System.Windows.Controls.ListBox>().Single();
                var scroller = (System.Windows.Controls.ScrollViewer)VisualTreeHelper.GetChild(list, 0);
                model.Selection = model.Clips[30].Id;
                Snapshots.Pump(0.2);
                var before = scroller.VerticalOffset;
                Check(before > 500, $"scrolled down to the selection ({before:0})");
                model.Delete(model.Clips[30]);
                Snapshots.Pump(0.5);
                Check(Math.Abs(scroller.VerticalOffset - before) < 50, $"still at {scroller.VerticalOffset:0}, was {before:0}");
                model.Query = "item";
                Snapshots.Pump(0.2);
                Check(scroller.VerticalOffset == 0, $"a new search starts at the top ({scroller.VerticalOffset:0})");
            }
            finally { host.Close(); }
        }),

        ("32-bit clipboard bitmaps from .NET apps become PNGs", () =>
        {
            foreach (var masks in new[] { false, true })
            {
                Reset();
                var r = new Recorder(new Db());
                Check(r.Record(new Snapshot(Array.Empty<string>(), null, null, Dib32(3, 2, masks)), "Paint", DateTime.Now), "recorded");
                using var db = new Db();
                var frame = BitmapDecoder.Create(new Uri(db.Clips().Single().File!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Check(frame.PixelWidth == 3 && frame.PixelHeight == 2, $"size {frame.PixelWidth}x{frame.PixelHeight} (color masks: {masks})");
            }
        }),

        ("a clip waiting to be deleted stays hidden when the list reloads", () =>
        {
            using (var db = new Db()) { db.Upsert(ClipKind.Text, "keep", null, "Notepad", "t:keep2"); db.Upsert(ClipKind.Text, "going", null, "Notepad", "t:going"); }
            var model = new Model();
            model.Refresh();
            using (Sql.Hold(Paths.Db, "BEGIN IMMEDIATE"))
            {
                model.Delete(model.Clips.Single(c => c.Text == "going"));
                model.Query = "o";
                Check(model.Clips.All(c => c.Text != "going") && model.Recent.All(c => c.Text != "going"), "not shown again while the database is busy, so it can't be deleted twice");
                Check(model.DeletesWaiting, "quitting now would wait for it");
            }
            Snapshots.Pump(0.5);
            Check(model.Clips.Select(c => c.Text).SequenceEqual(new[] { "keep" }), "deleted once the database is free");
            Check(!model.DeletesWaiting, "nothing left to wait for");
        }),

        ("quitting waits for a delete that's still queued", () =>
        {
            var writer = new Writer();
            var done = false;
            writer.Write(_ => { Thread.Sleep(300); done = true; });
            Check(writer.Finish(TimeSpan.FromSeconds(5)) && done, "finished before returning");
        }),

        ("ClipBubble.exe --peer relays ssh to the running app", () =>
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "ClipBubble.exe");
            var e = new Engine("Windows test");
            e.Start();
            Thread.Sleep(300);
            // Same as HostLink: keep .NET Framework from writing a byte order mark down the pipe.
            Console.InputEncoding = new UnicodeEncoding(false, false);
            using var p = Process.Start(new ProcessStartInfo(exe, "--peer") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var stderr = new StringBuilder();
            p.ErrorDataReceived += (_, x) => { if (x.Data != null) lock (stderr) stderr.AppendLine(x.Data); };
            p.BeginErrorReadLine();
            var frames = Codec.Encode(new Hello(SyncProto.Version, "relay-peer", "Relay", "linux", "1.4.0", Engine.Now)).Concat(Codec.Encode(new Summary(Array.Empty<Stamp>()))).ToArray();
            p.StandardInput.BaseStream.Write(frames, 0, frames.Length);
            p.StandardInput.BaseStream.Flush();
            var reader = new FrameReader();
            var got = new List<Frame>();
            var read = new Thread(() =>
            {
                var buf = new byte[65536];
                int n;
                while (!got.OfType<Summary>().Any() && (n = p.StandardOutput.BaseStream.Read(buf, 0, buf.Length)) > 0) got.AddRange(reader.Feed(buf, n));
            }) { IsBackground = true };
            read.Start();
            Check(read.Join(10_000), "the app answers through the relay (a GUI exe still gets ssh's stdio)");
            var log = Path.Combine(Paths.Dir, "sync.log");
            Check(got.OfType<Hello>().SingleOrDefault() is { Os: "windows", Name: "Windows test" },
                $"its hello; got [{string.Join(", ", got.Select(f => f is Hello h ? $"hello {h.Os} {h.Name}" : f.Type.ToString()))}]"
                + $", relay {(p.WaitForExit(1000) ? "exited " + p.ExitCode : "still running")}, stderr: {stderr.ToString().Trim()}"
                + $", sync.log: {(File.Exists(log) ? File.ReadAllText(log).Trim() : "none")}");
            Check(SpinWait.SpinUntil(() => e.Linked.Any(h => h.Name == "Relay"), 5000), "the app sees the peer");
            p.StandardInput.Close();
            Check(p.WaitForExit(10_000), "the relay exits when ssh closes");
            Check(SpinWait.SpinUntil(() => e.Linked.Count == 0, 5000), "the app drops the session");
        }),

        ("copies from other machines don't echo into history", () =>
        {
            using var db = new Db();
            var r = new Recorder(db);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var hash = "t:" + Store.Sha("from the mac");
            db.Insert(new Entry(hash, now - 1, "text", "from the mac", "Safari", "mac-1", Array.Empty<Blob>()), "mac-1");
            Recorder.Expect(hash, null);
            Check(!r.Record(Text("from the mac"), "Windows", DateTime.Now, ours: true), "the synced copy is left alone");
            Check(db.Clips().Single().Time == now - 1 && db.Clips().Single().Source == "Safari", "not bumped or renamed");

            // A synced file now lives in Downloads\ClipBubble: picking it moves that item up, not a new one.
            var local = Path.Combine(Paths.Dir, "a.pdf");
            File.WriteAllText(local, "%PDF");
            db.Insert(new Entry("f:remote", now - 100, "file", "a.pdf", "Finder", "mac-1", new[] { new Blob("a.pdf", 4, "") }), "mac-1");
            db.Received("f:remote", text: local);
            var c = db.ByHash("f:remote")!;
            Recorder.Expect(Recorder.CaptureHash(c), c.Hash);
            Check(r.Record(new Snapshot(new[] { local }, null, null, null), "Windows", DateTime.Now, ours: true), "the list changes");
            Check(db.Clips().Count == 2 && db.ByHash("f:remote")!.Time > now - 5, "the synced item moved up instead of a copy appearing");
        }),

        ("copying the previous item again after deleting it saves it", () =>
        {
            using var db = new Db();
            var r = new Recorder(db);
            var t0 = DateTime.Now;
            r.Record(Text("first"), "Notepad", t0);
            r.Record(Text("second"), "Notepad", t0.AddSeconds(5));
            db.Delete(db.Clips().Single(c => c.Text == "first").Id);
            r.Record(Text("first"), "Notepad", t0.AddSeconds(6));
            var texts = db.Clips().Select(c => c.Text).OrderBy(t => t).ToList();
            Check(texts.SequenceEqual(new[] { "first", "second" }), "both kept: " + string.Join(",", texts));
        }),

        ("a folder that shows up after the start reports only files saved since", () =>
        {
            var folder = Path.Combine(Paths.Dir, "drive-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var seen = new List<string>();
            using var watcher = new FolderWatcher(folder, null, _ => true, (path, _) => seen.Add(Path.GetFileName(path)));
            Directory.CreateDirectory(folder);   // like a drive being plugged back in, with old files on it
            var old = Path.Combine(folder, "old.pdf");
            File.WriteAllText(old, "x");
            File.SetCreationTime(old, DateTime.Now.AddDays(-2));
            File.WriteAllText(Path.Combine(folder, "new.pdf"), "x");
            Snapshots.Pump(6);   // the watcher re-checks for a missing folder every 5 s
            Check(seen.SequenceEqual(new[] { "new.pdf" }), "reported: " + string.Join(",", seen));
        }),

        ("removals saved by 1.3.0 in forgotten.json move into the shared table once", () =>
        {
            var downloads = Path.Combine(Paths.Dir, "legacy-downloads");
            Directory.CreateDirectory(downloads);
            var removed = Path.Combine(Paths.Dir, "Screenshot old.png");
            var downloadedAgain = Path.Combine(Paths.Dir, "report.pdf");
            File.WriteAllText(downloadedAgain, "a new copy, saved after it was removed");
            var beforeClear = Path.Combine(downloads, "before.pdf");
            var afterClear = Path.Combine(downloads, "after.pdf");
            File.WriteAllText(beforeClear, "x");
            File.WriteAllText(afterClear, "x");
            File.SetCreationTime(beforeClear, DateTime.Now.AddHours(-2));
            var clearedAt = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
            var json = Path.Combine(Paths.Dir, "forgotten.json");
            File.WriteAllText(json, new JavaScriptSerializer().Serialize(new { ClearedAt = clearedAt, Files = new Dictionary<string, long> { [removed] = clearedAt + 1000, [downloadedAgain] = clearedAt + 2000 } }));
            using var db = new Db();
            var folders = new[] { downloads, null, Path.Combine(Paths.Dir, "not there") };
            Check(db.ImportLegacyForgotten(folders), "moved");
            var hashes = Sql.Query(Paths.Db, "SELECT hash FROM forgotten ORDER BY hash");
            var expected = new[] { removed, beforeClear }.Select(f => "f:" + Store.Sha(f)).OrderBy(h => h, StringComparer.Ordinal).ToList();
            Check(hashes.SequenceEqual(expected), "the listed file and the one from before Clear all: " + hashes.Count);
            Check(File.Exists(json), "kept while a folder isn't there (an unplugged drive), so its Clear all still applies later");
            Directory.CreateDirectory(Path.Combine(Paths.Dir, "not there"));
            Check(db.ImportLegacyForgotten(folders), "moved again once it is");
            Check(!File.Exists(json), "and then the old file is gone");
            Check(Sql.Query(Paths.Db, $"SELECT CAST(at * 1000 AS INTEGER) FROM forgotten WHERE hash = 'f:{Store.Sha(removed)}'")[0] == (clearedAt + 1000).ToString(), "kept with when it was removed");
            Check(db.ImportLegacyForgotten(folders), "nothing left to move");
            File.WriteAllText(json, "{}");
            File.SetAttributes(json, FileAttributes.ReadOnly);
            try { Check(db.ImportLegacyForgotten(folders), "a file that can be read but not deleted doesn't hold the import back"); }
            finally { File.SetAttributes(json, FileAttributes.Normal); }
            using (new FileStream(json, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!db.ImportLegacyForgotten(folders), "a file that can't be read yet holds the import back");
            Check(File.Exists(json), "and is kept for next time");
        }),

        ("a screen recording still being saved doesn't hold up a copied picture", () =>
        {
            var recording = Path.Combine(Paths.Dir, "Screen Recording 1.mp4");
            using var db = new Db();
            using var saving = new FileStream(recording, FileMode.Create, FileAccess.Write, FileShare.Read);
            db.InsertIfMissing(ClipKind.File, recording, "Screenshot", "f:" + Store.Sha(recording), DateTime.Now);
            var watch = Stopwatch.StartNew();
            Check(new Recorder(db).Record(new Snapshot(Array.Empty<string>(), null, TinyPng(6), null), "Paint", DateTime.Now), "saved");
            Check(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
        }),

        ("file icons load in the background, and a missing one takes no space", () =>
        {
            var doc = Path.Combine(Paths.Dir, "icon-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".pdf");
            File.WriteAllText(doc, "%PDF");
            BitmapSource? icon = null;
            var calls = 0;
            Shell.Icon(doc, 64, b => { icon = b; calls++; });
            Check(calls == 0, "returned before the shell drew it");
            Snapshots.Pump(1);
            Check(calls == 1 && icon != null, "drawn and handed back");
            var missing = Shell.IconImage(Path.Combine(Paths.Dir, "no such folder", "x.pdf"), 64);
            Snapshots.Pump(1);
            Check(missing.Visibility == Visibility.Collapsed, "no icon: collapsed");
        }),

        ("cutting long text never splits an emoji", () =>
        {
            Check(Store.Head("ab\U0001F600cd", 3) == "ab", "cut before the emoji, not through it");
            Check(Store.Head("ab\U0001F600cd", 4) == "ab\U0001F600", "or after it");
            Check(Store.Head("short", 10) == "short", "short text is left alone");
        }),

        ("search treats % and _ as plain characters", () =>
        {
            using var db = new Db();
            db.Upsert(ClipKind.Text, "50% off", null, "Notepad", "t:pct");
            db.Upsert(ClipKind.Text, "500 apples", null, "Notepad", "t:500");
            db.Upsert(ClipKind.Text, "my_file.txt", null, "Notepad", "t:under");
            db.Upsert(ClipKind.Text, "myXfile.txt", null, "Notepad", "t:x");
            db.Upsert(ClipKind.Text, @"C:\temp", null, "Notepad", "t:slash");
            Check(db.Clips(query: "50%").Select(c => c.Text).SequenceEqual(new[] { "50% off" }), "% is a percent sign");
            Check(db.Clips(query: "my_file").Select(c => c.Text).SequenceEqual(new[] { "my_file.txt" }), "_ is an underscore");
            Check(db.Clips(query: @"C:\t").Select(c => c.Text).SequenceEqual(new[] { @"C:\temp" }), "a backslash is a backslash");
        }),

        ("a copy over 2 MB isn't saved; long ones show and send only their start", () =>
        {
            using var db = new Db();
            var r = new Recorder(db);
            var huge = new string('x', Recorder.MaxTextBytes + 1);
            Check(!r.Record(Text(huge), "Notepad", DateTime.Now), "over 2 MB: skipped");
            Check(!r.Record(Text(new string('€', 700_000)), "Notepad", DateTime.Now.AddSeconds(1)), "700,000 euro signs are 2.1 MB in UTF-8: skipped too");
            var lines = string.Concat(Enumerable.Range(0, 30_000).Select(i => "line " + i + "\n"));
            Check(r.Record(Text(lines), "Notepad", DateTime.Now.AddSeconds(5)), "under 2 MB: saved");
            var clip = db.Clips().Single();
            var row = new Row(clip, 0, isSelected: false, new Model());
            var texts = Descendants(row).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text).ToList();
            Check(texts.Contains("line 0") && texts.Any(t => t.EndsWith("+ lines")), "the row reads only the start: " + string.Join(" | ", texts));
            var preview = new PreviewPane(clip);
            var shown = Descendants(preview).OfType<System.Windows.Controls.TextBox>().Single().Text;
            Check(shown.Length < 100_100 && shown.EndsWith("but it pastes in full"), $"the preview shows the start ({shown.Length} characters)");
            var got = (string)((Dictionary<string, object>)((System.Collections.IList)ClipBubble.Mcp.Call(db, "clipboard_get", new Dictionary<string, object?> { ["id"] = clip.Id })["content"])[0])["text"];
            Check(got.Length < 200_200 && got.EndsWith($"(cut: {lines.Length} characters in all)"), $"clipboard_get sends the start ({got.Length} characters)");
        }),

        ("a failed mascot import leaves nothing behind", () =>
        {
            string Err(Action a) { try { a(); return ""; } catch (MascotException e) { return e.Message; } }
            var missing = Path.Combine(Paths.Dir, "nowhere.png");
            Check(Err(() => Mascots.Add("Half", "image", null, new() { ["idle"] = Svg, ["hover"] = missing })) == $"couldn't read the hover image at {missing}", "says which image");
            Check(!Directory.EnumerateFiles(Mascots.Dir, "half*").Any(), "the idle SVG it already saved is gone too");
            var leftover = Path.Combine(Mascots.Dir, "fox-idle.png");
            File.WriteAllBytes(leftover, TinyPng());
            Err(() => Mascots.Add("Fox", "image", null, new() { ["idle"] = leftover, ["hover"] = missing }));
            Check(File.Exists(leftover), "an image that was already in the mascot folder is never deleted");
            var other = Path.Combine(Mascots.Dir, "owl-idle.png");
            File.WriteAllBytes(other, TinyPng());
            Err(() => Mascots.Add("Owl", "image", null, new() { ["idle"] = missing }));
            Check(File.Exists(other), "not even when a different image fails to copy over it");
        }),

        ("screen readers see the bubble as a ClipBubble button they can press", () =>
        {
            var bubble = new BubbleView(new Model());
            var pressed = 0;
            bubble.Pressed = () => pressed++;
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(bubble);
            Check(peer.GetName() == "ClipBubble" && peer.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Button, $"{peer.GetName()} / {peer.GetAutomationControlType()}");
            Check(peer.GetHelpText() == "Opens your clipboard history", "help text");
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            Check(pressed == 1, "pressing it does what a click does");
        }),

        ("a screenshot folder that's renamed and made again is watched again", () =>
        {
            var folder = Path.Combine(Paths.Dir, "renamed-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            var seen = new List<string>();
            using var watcher = new FolderWatcher(folder, null, _ => true, (path, _) => seen.Add(Path.GetFileName(path)));
            Directory.Move(folder, folder + "-old");
            Directory.CreateDirectory(folder);
            var restored = Path.Combine(folder, "restored.pdf");   // brought back by a sync tool, say: not new
            File.WriteAllText(restored, "x");
            File.SetCreationTime(restored, DateTime.Now.AddDays(-2));
            Thread.Sleep(50);
            Snapshots.Pump(6);   // checked every 5 s
            File.WriteAllText(Path.Combine(folder, "new.png"), "x");
            for (var i = 0; i < 20 && seen.Count == 0; i++) Snapshots.Pump(0.5);   // the check runs off the UI thread: give a busy runner time
            Check(seen.SequenceEqual(new[] { "new.png" }), "reported: " + string.Join(",", seen)
                + $"; same creation time as the renamed one: {Directory.GetCreationTimeUtc(folder) == Directory.GetCreationTimeUtc(folder + "-old")}"
                + $", thread pool answers: {Task.Run(() => 1).Wait(2000)}");
        }),

        ("MCP over stdio from the real exe: tools, history, image, mascots", Mcp),
    };

    static void Mcp()
    {
        using (var db = new Db())
        {
            db.Upsert(ClipKind.Text, "npm run dev", null, "Windows Terminal", "t:npm");
            var png = Path.Combine(Paths.Images, "x.png");
            File.WriteAllBytes(png, TinyPng());
            Sql.Exec(Paths.Db, $"INSERT INTO clips(kind,text,file,source,hash,created) VALUES('image','','{png}','Paint','i:x',{DateTimeOffset.UtcNow.ToUnixTimeSeconds()})");
        }
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClipBubble.exe");
        var start = new ProcessStartInfo(exe, "--mcp") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        start.EnvironmentVariables["CLIPBUBBLE_HOME"] = Home;
        using var p = Process.Start(start)!;
        var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var id = 0;
        Dictionary<string, object> Call(string method, object? prms = null)
        {
            stdin.WriteLine(json.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method, ["params"] = prms }));
            var line = p.StandardOutput.ReadLine() ?? throw new Exception("server closed");
            var reply = json.Deserialize<Dictionary<string, object>>(line);
            Check((int)reply["id"] == id, "id echoed");
            return (Dictionary<string, object>)reply["result"];
        }
        static object[] List(object o) => ((System.Collections.IList)o).Cast<object>().ToArray();
        string Tool(string name, object args) => (string)((Dictionary<string, object>)List(Call("tools/call", new { name, arguments = args })["content"])[0])["text"];
        try
        {
            stdin.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            stdin.WriteLine("[1,2]");
            stdin.WriteLine("\"not an object\"");
            stdin.WriteLine("   ");
            stdin.WriteLine("null");
            for (var i = 0; i < 3; i++)
            {
                var reply = json.Deserialize<Dictionary<string, object>>(p.StandardOutput.ReadLine() ?? throw new Exception("server closed"));
                Check(reply.ContainsKey("id") && reply["id"] == null && (int)((Dictionary<string, object>)reply["error"])["code"] == -32700, "parse error for a line that isn't a JSON object");
            }
            var init = Call("initialize", new { protocolVersion = "2025-06-18" });
            Check((string)((Dictionary<string, object>)init["serverInfo"])["name"] == "clipbubble", "server name");
            var tools = List(Call("tools/list")["tools"]).Select(t => (string)((Dictionary<string, object>)t)["name"]).ToArray();
            Check(tools.SequenceEqual(new[] { "clipboard_history", "clipboard_get", "clipboard_set", "mascot_list", "mascot_set", "mascot_add", "mascot_remove" }), "tools " + string.Join(",", tools));
            var history = Tool("clipboard_history", new { limit = 5 });
            Check(history.Contains("npm run dev") && history.Contains("· text · Windows Terminal ·"), history);
            Check(Tool("clipboard_history", new { kind = "image" }).Contains("x.png"), "image filter");
            var imageId = int.Parse(Tool("clipboard_history", new { kind = "image" }).Split(' ')[0].TrimStart('#'));
            var got = List(Call("tools/call", new { name = "clipboard_get", arguments = new { id = imageId } })["content"]);
            Check(got.Length == 2 && (string)((Dictionary<string, object>)got[1])["mimeType"] == "image/png", "image block returned");
            Check(Tool("mascot_add", new { name = "Pixel Fox", kind = "image", idle = Svg }) == "Added mascot pixel-fox.", "mascot_add");
            Check(Tool("mascot_list", new { }).Contains("pixel-fox · Pixel Fox · image · custom  ← active"), "mascot_list");
            Check(Tool("mascot_remove", new { id = "pixel-fox" }) == "Removed mascot pixel-fox.", "mascot_remove");
            Check(Tool("clipboard_get", new { id = 999999 }) == "No item with that id.", "missing id");
            stdin.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + (++id) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"clipboard_get\",\"arguments\":{\"id\":9223372036854775808}}}");
            var huge = p.StandardOutput.ReadLine() ?? throw new Exception("server died on an out-of-range id");
            Check(huge.Contains("No item with that id."), "out-of-range id is just a missing item: " + huge);
            Sql.Exec(Paths.Db, "ALTER TABLE clips RENAME TO gone");
            stdin.WriteLine(json.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call", ["params"] = new { name = "clipboard_history", arguments = new { } } }));
            var failed = json.Deserialize<Dictionary<string, object>>(p.StandardOutput.ReadLine() ?? throw new Exception("server died on a failing request"));
            Sql.Exec(Paths.Db, "ALTER TABLE gone RENAME TO clips");
            Check(failed.ContainsKey("error") && (int)failed["id"] == id, "a failing request gets an error reply");
            Check(Tool("clipboard_history", new { limit = 1 }).Contains("#"), "and the server keeps serving");
            stdin.Close();
            Check(p.WaitForExit(5000), "exits when stdin closes");
        }
        finally { if (!p.HasExited) p.Kill(); }
    }

    // MARK: - Fixtures

    public const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><rect x=\"2\" y=\"5\" width=\"12\" height=\"7\" fill=\"#E8762B\"/><rect x=\"5\" y=\"7\" width=\"2\" height=\"2\" fill=\"#222\"/></svg>";

    static Snapshot Text(string s) => new(Array.Empty<string>(), s, null, null);

    static IEnumerable<DependencyObject> Visuals(DependencyObject root) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root)).Select(i => VisualTreeHelper.GetChild(root, i)).SelectMany(c => new[] { c }.Concat(Visuals(c)));

    /// What .NET's Clipboard.SetImage puts on: 32 bits per pixel, optionally with BI_BITFIELDS color masks after the header.
    static byte[] Dib32(int w, int h, bool masks)
    {
        var header = 40 + (masks ? 12 : 0);
        var dib = new byte[header + w * h * 4];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        if (masks)
        {
            BitConverter.GetBytes(3).CopyTo(dib, 16);
            BitConverter.GetBytes(0x00FF0000).CopyTo(dib, 40);
            BitConverter.GetBytes(0x0000FF00).CopyTo(dib, 44);
            BitConverter.GetBytes(0x000000FF).CopyTo(dib, 48);
        }
        for (var i = header; i < dib.Length; i++) dib[i] = 0x80;
        return dib;
    }

    /// WIC fails on this with a COMException, which .NET doesn't map to a file-format error.
    static byte[] BrokenWebp()
    {
        var bytes = new byte[116];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.GetBytes(100).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("WEBPVP8 ").CopyTo(bytes, 8);
        var noise = new byte[100];
        new Random(1).NextBytes(noise);
        noise.CopyTo(bytes, 16);
        return bytes;
    }

    /// Settings needs an App for its shortcut label; WPF allows one per process. Closing a test window mustn't end it.
    static readonly Lazy<App> TestApp = new(() => new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, Headless = true });

    static IEnumerable<DependencyObject> Descendants(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    static IEnumerable<System.Windows.Controls.Border> Switches(DependencyObject root) =>
        Descendants(root).OfType<System.Windows.Controls.Border>().Where(b => b.Width == 26 && b.Height == 15);

    static void Click(UIElement e) =>
        e.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    /// Same pixels, different PNG bytes, like a screenshot tool's copy versus its saved file.
    static byte[] Reencode(byte[] png)
    {
        var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.On };
        enc.Frames.Add(BitmapFrame.Create(new MemoryStream(png)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static string Squash(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("( ", "(").Replace(" )", ")");

    public static byte[] TinyPng(int seed = 1)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(seed * 40); pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static byte[] Dib24(int w, int h)
    {
        var stride = (w * 3 + 3) & ~3;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        for (var i = 40; i < dib.Length; i++) dib[i] = 0x80;
        return dib;
    }

    static bool Colorful(BitmapSource? img)
    {
        if (img == null) return false;
        var px = new byte[img.PixelWidth * img.PixelHeight * 4];
        new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0).CopyPixels(px, img.PixelWidth * 4, 0);
        for (var i = 0; i < px.Length; i += 4)
            if (px[i + 3] > 200 && Math.Max(Math.Abs(px[i] - px[i + 1]), Math.Abs(px[i + 1] - px[i + 2])) > 60) return true;
        return false;
    }
}

/// A clipboard that lists `formats` but answers whatever `data` returns, like Windows synthesizing formats on request.
sealed class FakeClipboard(string[] formats, Func<string, object?> data) : IDataObject
{
    public string[] GetFormats() => formats;
    public string[] GetFormats(bool autoConvert) => formats;
    public object? GetData(string format) => data(format);
    public object? GetData(string format, bool autoConvert) => data(format);
    public object? GetData(Type format) => null;
    public bool GetDataPresent(string format) => data(format) != null;
    public bool GetDataPresent(string format, bool autoConvert) => data(format) != null;
    public bool GetDataPresent(Type format) => false;
    public void SetData(object data) { }
    public void SetData(string format, object data) { }
    public void SetData(string format, object data, bool autoConvert) { }
    public void SetData(Type format, object data) { }
}

/// Raw SQLite for fixtures and schema checks, outside the app's own Db.
static class Sql
{
    public static List<string> Query(string path, string sql)
    {
        sqlite3_open16(path, out var db);
        try
        {
            sqlite3_prepare16_v2(db, sql, -1, out var s, IntPtr.Zero);
            var rows = new List<string>();
            while (sqlite3_step(s) == 100) rows.Add(Marshal.PtrToStringUni(sqlite3_column_text16(s, 0)) ?? "");
            sqlite3_finalize(s);
            return rows;
        }
        finally { sqlite3_close_v2(db); }
    }

    public static void Exec(string path, string sql) => Query(path, sql);

    /// Keeps a second connection open with `sql` applied (for example a write lock) until disposed.
    public static IDisposable Hold(string path, string sql)
    {
        sqlite3_open16(path, out var db);
        sqlite3_prepare16_v2(db, sql, -1, out var s, IntPtr.Zero);
        sqlite3_step(s);
        sqlite3_finalize(s);
        return new Closer(() => sqlite3_close_v2(db));
    }

    sealed class Closer(Action close) : IDisposable { public void Dispose() => close(); }

    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_open16(string path, out IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr s);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text16(IntPtr s, int i);
}

/// Renders the real views offscreen at 2x with the same items as Assets/press/panel.png and peek.png.
static class Snapshots
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var fixtures = Path.Combine(Path.GetDirectoryName(Paths.Db)!, "fixtures");
        Directory.CreateDirectory(fixtures);
        var shot = Path.Combine(fixtures, "Screenshot 2026-10-14 214107.png");
        File.WriteAllBytes(shot, Bars(640, 400));
        var photo = Path.Combine(Paths.Images, "sunset.png");
        Directory.CreateDirectory(Paths.Images);
        File.WriteAllBytes(photo, Sunset(1200));
        var pdf = Path.Combine(fixtures, "Pitch Deck v3.pdf");
        File.WriteAllText(pdf, "%PDF-1.4");

        var apps = new Dictionary<string, string>();
        void App(string name, string path) { path = Environment.ExpandEnvironmentVariables(path); if (File.Exists(path)) apps[name] = path; }
        App("Windows Terminal", @"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe");
        App("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        App("File Explorer", @"C:\Windows\explorer.exe");
        App("Cursor", @"%LOCALAPPDATA%\Programs\cursor\Cursor.exe");
        App("Visual Studio Code", @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe");
        File.WriteAllText(Path.Combine(Paths.Dir, "apps.json"), new JavaScriptSerializer().Serialize(apps));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void Row(string kind, string text, string? file, string source, int minutesAgo) =>
            Sql.Exec(Paths.Db, $"INSERT INTO clips(kind,text,file,source,hash,created) VALUES('{kind}','{text.Replace("'", "''")}',{(file == null ? "NULL" : $"'{file}'")},'{source}','{Guid.NewGuid()}',{now - minutesAgo * 60 - 1})");
        using (new Db()) { }
        Row("text", "Meet at 7? Blue Tokai, Indiranagar", null, "WhatsApp", 200);
        Row("text", "func paste(_ clip: Clip) {\n  board.write(clip)\n  hide()\n}", null, "Cursor", 120);
        Row("file", pdf, null, "File Explorer", 60);
        Row("text", "#FF7AB6", null, "Figma", 26);
        Row("image", "", photo, "Snipping Tool", 14);
        Row("text", "https://krnl.studio", null, "Microsoft Edge", 9);
        Row("file", shot, null, "Screenshot", 3);
        Row("text", "npm run dev", null, "Windows Terminal", 0);

        var app = new App();
        var model = new Model();
        model.Reload();
        model.ReloadRecent();
        Pump(1.0);   // thumbnails decode on the thread pool

        var panel = new PanelView(model, () => new SettingsView(model, app));
        model.Selection = model.Clips[1].Id;
        Save(Framed(panel, 16, 420, 470), Path.Combine(dir, "panel.png"));
        model.ShowSettings = true;
        Pump(0.3);
        Save(Framed(panel, 16, 420, 470), Path.Combine(dir, "settings.png"));
        model.ShowSettings = false;

        var peek = new PeekView(model, () => "Ctrl+Shift+Space");
        Save(Framed(peek, 14, 300, double.NaN), Path.Combine(dir, "peek.png"));

        // Every other state the panel can be in.
        void State(string name, Action set, Action reset)
        {
            set();
            Pump(0.6);
            Save(Framed(panel, 16, 420, 470), Path.Combine(dir, name + ".png"));
            reset();
        }
        foreach (var (name, pick) in new (string, Func<Clip, bool>)[] { ("preview-image", c => c.Kind == ClipKind.Image), ("preview-file", c => c.Text.EndsWith(".pdf")), ("preview-text", c => c.Text.StartsWith("func")) })
            State(name, () => { model.Selection = model.Clips.First(pick).Id; model.Previewing = true; }, () => model.Previewing = false);
        State("panel-no-matches", () => model.Query = "zzzz", () => model.Query = "");
        State("panel-filter-screenshots", () => model.Filter = "shots", () => model.Filter = "all");
        State("panel-search", () => model.Query = "krnl", () => model.Query = "");
        model.Unseen = 120;
        var busy = new BubbleView(model) { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)) };
        model.Unseen = 121;
        Pump(0.8);
        Save(busy, Path.Combine(dir, "bubble-badge-99.png"));
        model.Unseen = 0;
        using (var db = new Db()) db.DeleteAll();
        model.Reload();
        model.ReloadRecent();
        State("panel-empty", () => { }, () => { });
        Save(Framed(new PeekView(model, () => "Ctrl+Shift+Space"), 14, 300, double.NaN), Path.Combine(dir, "peek-empty.png"));

        foreach (var m in Mascots.Builtins)
        {
            Mascots.SetActive(m.Id);
            model.LoadMascots();
            foreach (var (mood, hover, happy) in new[] { ("idle", false, false), ("hover", true, false), ("active", false, true) })
            {
                var bubble = new BubbleView(model) { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)) };
                bubble.SetMood(hover, false, happy);
                Pump(0.8);
                Save(bubble, Path.Combine(dir, $"mascot-{m.Id}-{mood}.png"));
            }
        }
        Console.WriteLine("snapshots in " + dir);
    }

    static FrameworkElement Framed(FrameworkElement view, double radius, double width, double height)
    {
        if (view.Parent is System.Windows.Controls.Panel old) old.Children.Remove(view);
        var glass = Glass.Wrap(view, radius, shadow: false);
        glass.Width = width;
        if (!double.IsNaN(height)) glass.Height = height;
        return glass;
    }

    public static void Pump(double seconds)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    /// Hosted in an offscreen window, because the history list only creates rows once it's laid out in one.
    static void Save(FrameworkElement e, string path)
    {
        var host = new Window { Content = e, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight };
        host.Show();
        e.UpdateLayout();
        Pump(0.3);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * 2), (int)Math.Ceiling(e.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        rtb.Render(e);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(path)) enc.Save(f);
        host.Content = null;
        host.Close();
    }

    static byte[] Encode(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static byte[] Bars(int w, int h)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22)), null, new Rect(0, 0, w, h));
            var colors = new[] { Color.FromRgb(0xE5, 0x3E, 0x5A), Color.FromRgb(0xEE, 0x8A, 0x2E), Color.FromRgb(0xB0, 0x3C, 0xD6) };
            for (var i = 0; i < 3; i++) dc.DrawRectangle(new SolidColorBrush(colors[i]), null, new Rect(40 + i * 190, 40, 170, 180));
            for (var i = 0; i < 4; i++) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)), null, new Rect(40, 250 + i * 30, 560 - i * 90, 12));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        return Encode(rtb);
    }

    static byte[] Sunset(int size)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xF7, 0x8C, 0x6B), Color.FromRgb(0xFB, 0xD2, 0x8B), 90), null, new Rect(0, 0, size, size));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x7A)), null, new Point(size / 2.0, size * 0.55), size * 0.12, size * 0.12);
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new Point(0, size * 0.75), true, true);
                g.LineTo(new Point(size * 0.35, size * 0.55), true, false);
                g.LineTo(new Point(size * 0.6, size * 0.7), true, false);
                g.LineTo(new Point(size * 0.8, size * 0.58), true, false);
                g.LineTo(new Point(size, size * 0.72), true, false);
                g.LineTo(new Point(size, size), true, false);
                g.LineTo(new Point(0, size), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2B, 0x1E, 0x3A)), null, hills);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        return Encode(rtb);
    }
}
