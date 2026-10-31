using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace ClipBubble.SyncTests;

/// Plays a linked device by hand, so a test can send what no honest engine would.
sealed class RawPeer : IDisposable
{
    readonly AnonymousPipeServerStream toEngine = new(PipeDirection.Out), fromEngine = new(PipeDirection.In);
    readonly List<Frame> frames = new();
    readonly FrameReader reader = new();
    readonly Engine engine;
    readonly Session session;

    public RawPeer(Engine e, string device = "raw-peer", IReadOnlyList<Stamp>? summary = null)
    {
        engine = e;
        session = new Session(new AnonymousPipeClientStream(PipeDirection.In, toEngine.ClientSafePipeHandle),
            new AnonymousPipeClientStream(PipeDirection.Out, fromEngine.ClientSafePipeHandle), e);
        new Thread(Read) { IsBackground = true }.Start();
        e.Post(() => e.Begin(session));
        Send(new Hello(SyncProto.Version, device, "Lost iPhone", "ios", "1.6.2", Engine.Now));
        Send(new Summary(summary ?? Array.Empty<Stamp>()));
        First(f => f is Summary);
    }

    void Read()
    {
        var buf = new byte[1 << 20];
        try
        {
            int n;
            while ((n = fromEngine.Read(buf, 0, buf.Length)) > 0)
                lock (frames) frames.AddRange(reader.Feed(buf, n));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public void Send(Frame f)
    {
        var data = Codec.Encode(f);
        toEngine.Write(data, 0, data.Length);
        toEngine.Flush();
    }

    public Frame? First(Func<Frame, bool> match)
    {
        Frame? hit = null;
        Program.WaitFor(() => { lock (frames) hit = frames.FirstOrDefault(match); return hit != null; }, 8);
        return hit;
    }

    public Entry? Put(string hash) => First(f => f is Entry e && e.Hash == hash) as Entry;

    public bool Has(string hash) { lock (frames) return frames.Any(f => f is Entry e && e.Hash == hash); }

    /// The bytes of a chunk, or null for a cancel.
    public byte[]? Fetch(string hash, int index = 0, long offset = 0)
    {
        Send(new Fetch(hash, index, offset));
        var reply = First(f => f is Chunk c && c.Hash == hash || f is Cancel x && x.Hash == hash);
        Program.Check(reply != null, "the engine answers a fetch of " + hash);
        lock (frames) frames.Remove(reply!);
        return (reply as Chunk)?.Data;
    }

    public void Dispose()
    {
        engine.Post(session.Close);
        toEngine.Dispose();
    }
}

/// docs/sync-protocol.md: a device reaches only files ClipBubble offered it from history.
static class Boundary
{
    public static void Run()
    {
        var home = Path.Combine(Paths.Dir, "boundary");
        Directory.CreateDirectory(home);
        var secret = Path.Combine(home, "secret.txt");
        File.WriteAllText(secret, "not for peers");
        var link = Path.Combine(home, "innocent.txt");
        File.CreateSymbolicLink(link, secret);
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(home);
        using var db = new Db();
        var e = new Engine("Windows test");
        var applied = new List<string>();
        e.Apply = c => { lock (applied) applied.Add(c.Hash); };
        try
        {
            using (var peer = new RawPeer(e))
            {
                var names = new[] { secret, "../boundary/secret.txt", "./secret.txt", "secret.txt", "innocent.txt", link, "~/secret.txt", "file://" + secret,
                    "//" + secret, "/" + secret, " " + secret, "safe.txt\n" + secret, "\\" + secret, secret + "\r", "\\\\server\\share\\secret.txt", "C:secret.txt", "C:\\secret.txt" };
                for (var i = 0; i < names.Length; i++)
                {
                    var hash = "f:probe" + i;
                    peer.Send(new Entry(hash, Engine.Now, "file", names[i], "Explorer", e.Device, Array.Empty<Blob>()));
                    Program.Check(peer.Fetch(hash) == null, "a peer's text is never a path: " + names[i]);
                    Program.Check(peer.Fetch(hash, 1) == null, "nor is a later index: " + names[i]);
                    Program.Check(db.ByHash(hash)!.Paths.All(p => p.IndexOfAny(new[] { '/', '\\', ':' }) < 0), "the row keeps bare names: " + db.ByHash(hash)!.Text);
                }
                lock (applied) Program.Check(applied.Count == 0, "a file item with no bytes never lands on the clipboard");

                peer.Send(new Entry("f:waiting", Engine.Now, "file", secret, "Explorer", "elsewhere", new[] { new Blob("../../secret.txt", 5, "") }));
                Program.Check(peer.Fetch("f:waiting") == null, "a row still waiting for its bytes can't be fetched");
                Program.Check(db.ByHash("f:waiting")!.Text == Engine.SafeFileName(secret), "and holds a bare name");
                peer.Send(new Cancel("f:waiting"));
            }

            var mine = Path.Combine(home, "mine.txt");
            File.WriteAllText(mine, "offered");
            var mineHash = "f:" + Store.Sha(mine);
            db.Upsert(ClipKind.File, mine, null, "Explorer", mineHash);

            using (var liar = new RawPeer(e, "liar", new[] { db.Stamp(mineHash)! }))
            {
                Program.Check(liar.Fetch(mineHash) == null, "claiming to know an item in a summary doesn't offer it");
                Program.Check(liar.Fetch("f:" + Store.Sha(secret)) == null, "the hash of something never in history gets nothing");
            }

            using (var honest = new RawPeer(e, "honest"))
            {
                Program.Check(honest.Put(mineHash)?.Text == "mine.txt", "an item in history is offered by name, not by path");
                Program.Check(honest.Fetch(mineHash)?.SequenceEqual(File.ReadAllBytes(mine)) == true, "and an offered item comes over");
                Program.Check(honest.Fetch(mineHash, 0, -1) == null, "a negative offset gets nothing");
                Program.Check(honest.Fetch(mineHash, 0, 1 << 20) == null, "nor does one past the end");
                Program.Check(honest.Fetch(mineHash, int.MaxValue) == null, "nor does an index past the end");
                db.Delete(mineHash);
                Program.Check(honest.Fetch(mineHash) == null, "a delete takes it back at once");
            }

            var shot = Path.Combine(home, "shot.txt");
            File.WriteAllText(shot, "hidden");
            var shotHash = "f:" + Store.Sha(shot);
            db.Upsert(ClipKind.File, shot, null, "Explorer", shotHash);
            using (var early = new RawPeer(e, "early"))
            {
                Program.Check(early.Put(shotHash) != null, "offered while its switch is on");
                Prefs.SetOn("files", false);
                Program.Check(early.Fetch(shotHash) == null, "switching a category off takes back what was offered");
                using var hidden = new RawPeer(e, "hidden");
                Program.Check(hidden.Fetch(shotHash) == null, "and it isn't offered to the next device");
            }
            Prefs.SetOn("files", true);

            var pic = Path.Combine(home, "relayed.png");
            File.WriteAllText(pic, "relayed picture");
            var picHash = "i:" + Store.Sha(File.ReadAllBytes(pic));
            db.Insert(new Entry(picHash, Engine.Now - 60, "image", "", "Preview", "elsewhere", new[] { new Blob("relayed.png", 15, picHash.Substring(2)) }), "elsewhere");
            var marker = "after the waiting row " + Engine.Now;
            db.Upsert(ClipKind.Text, marker, null, "Notepad", "t:" + Store.Sha(marker));
            using (var third = new RawPeer(e, "third"))
            {
                Program.Check(third.Put("t:" + Store.Sha(marker)) != null, "the third device gets what this one holds");
                Program.Check(!third.Has(picHash), "but no item with its bytes still on the way");
                db.Received(picHash, pic);
                Program.Check(third.Put(picHash)?.Blobs.Count == 1, "it's offered as soon as they arrive");
                Program.Check(third.Fetch(picHash)?.SequenceEqual(File.ReadAllBytes(pic)) == true, "and comes over");
            }

            var big = new string('x', SyncProto.MaxFrame + 1);
            var after = "after the big row " + Engine.Now;
            using (var peer = new RawPeer(e, "big"))
            {
                db.Upsert(ClipKind.Text, big, null, "Notepad", "t:" + Store.Sha(big));
                db.Upsert(ClipKind.Text, after, null, "Notepad", "t:" + Store.Sha(after));
                Program.Check(peer.Put("t:" + Store.Sha(after)) != null, "a row too big to sync doesn't stop the next copy");
                Program.Check(!peer.Has("t:" + Store.Sha(big)), "and stays here");
            }
        }
        finally
        {
            Prefs.SetOn("files", true);
            Directory.SetCurrentDirectory(cwd);
            db.DeleteAll();
        }
    }
}
