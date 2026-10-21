using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClipBubble;

// The device-sync model and wire format shared with the Mac and Linux apps (docs/sync-protocol.md).

static class SyncProto
{
    public const int Version = 1;
    public const int MaxFrame = 4 << 20;
    public const int ChunkSize = 256 << 10;
    public const long AutoFetchLimit = 25 << 20;
}

sealed class Malformed() : Exception("malformed frame");

sealed record Stamp(string Hash, double Time, bool Deleted)
{
    /// Later wins. On a tie a delete wins, so taking something back never loses to a race.
    public bool Beats(Stamp? other) =>
        other == null || Time > other.Time || (Time == other.Time && Deleted && !other.Deleted);
}

sealed record Blob(string Name, long Size, string Sha);

enum FrameType : byte { Hello = 1, Summary, Put, Tombstone, Fetch, Chunk, Cancel, Applied, Ping, Pong, Offer, Device }

abstract record Frame
{
    public abstract FrameType Type { get; }
}

sealed record Hello(int Proto, string Device, string Name, string Os, string Version, double Clock) : Frame
{
    public override FrameType Type => FrameType.Hello;

    /// Whether the peer reads device frames. Earlier versions close the link on a frame type they don't know.
    /// A build without a release number (a dev or test build) runs this code, so it does.
    public bool ReadsDevices
    {
        get
        {
            var parts = Version.Split('.');
            if (!parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return true;
            int[] since = [1, 6, 2];
            for (var i = 0; i < Math.Max(parts.Length, since.Length); i++)
            {
                var have = i < parts.Length ? int.Parse(parts[i]) : -1;
                var want = i < since.Length ? since[i] : -1;
                if (have != want) return have > want;
            }
            return true;
        }
    }
}

sealed record Summary(IReadOnlyList<Stamp> Stamps) : Frame
{
    public override FrameType Type => FrameType.Summary;
    public bool Equals(Summary? o) => o != null && Stamps.SequenceEqual(o.Stamps);
    public override int GetHashCode() => Stamps.Count;
}

/// A put: one history item. File and image bytes follow on request (fetch and chunk).
sealed record Entry(string Hash, double Time, string Kind, string Text, string Source, string Origin, IReadOnlyList<Blob> Blobs) : Frame
{
    public override FrameType Type => FrameType.Put;
    public Stamp Stamp => new(Hash, Time, false);
    public long Size => Blobs.Sum(b => b.Size);
    public bool Equals(Entry? o) => o != null && (Hash, Time, Kind, Text, Source, Origin) == (o.Hash, o.Time, o.Kind, o.Text, o.Source, o.Origin) && Blobs.SequenceEqual(o.Blobs);
    public override int GetHashCode() => Hash.GetHashCode();
}

sealed record Tombstone(Stamp Stamp) : Frame { public override FrameType Type => FrameType.Tombstone; }
sealed record Fetch(string Hash, int Index, long Offset) : Frame { public override FrameType Type => FrameType.Fetch; }

sealed record Chunk(string Hash, int Index, long Offset, long Total, byte[] Data) : Frame
{
    public override FrameType Type => FrameType.Chunk;
    public bool Equals(Chunk? o) => o != null && (Hash, Index, Offset, Total) == (o.Hash, o.Index, o.Offset, o.Total) && Data.SequenceEqual(o.Data);
    public override int GetHashCode() => Hash.GetHashCode();
}

sealed record Cancel(string Hash) : Frame { public override FrameType Type => FrameType.Cancel; }
sealed record Applied(string Hash) : Frame { public override FrameType Type => FrameType.Applied; }
/// "Fetch this now, whatever its size": Send to.
sealed record Offer(string Hash) : Frame { public override FrameType Type => FrameType.Offer; }
/// A device the sender knows, maybe only through another link, so items relayed from it show its name.
sealed record DeviceInfo(string Id, string Name, string Os) : Frame { public override FrameType Type => FrameType.Device; }
sealed record Ping(double At) : Frame { public override FrameType Type => FrameType.Ping; }
sealed record Pong(double At) : Frame { public override FrameType Type => FrameType.Pong; }

static class Codec
{
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(Frame f)
    {
        var w = new Writer();
        w.U8((byte)f.Type);
        switch (f)
        {
            case Hello h: w.U32((uint)h.Proto); w.Str(h.Device); w.Str(h.Name); w.Str(h.Os); w.Str(h.Version); w.F64(h.Clock); break;
            case Summary s: w.U32((uint)s.Stamps.Count); foreach (var st in s.Stamps) w.Stamp(st); break;
            case Entry e:
                w.Str(e.Hash); w.F64(e.Time); w.Str(e.Kind); w.Str(e.Text); w.Str(e.Source); w.Str(e.Origin);
                w.U32((uint)e.Blobs.Count);
                foreach (var b in e.Blobs) { w.Str(b.Name); w.I64(b.Size); w.Str(b.Sha); }
                break;
            case Tombstone t: w.Stamp(t.Stamp); break;
            case Fetch x: w.Str(x.Hash); w.U32((uint)x.Index); w.I64(x.Offset); break;
            case Chunk c: w.Str(c.Hash); w.U32((uint)c.Index); w.I64(c.Offset); w.I64(c.Total); w.Bytes(c.Data); break;
            case Cancel c: w.Str(c.Hash); break;
            case Applied a: w.Str(a.Hash); break;
            case Offer o: w.Str(o.Hash); break;
            case Ping p: w.F64(p.At); break;
            case Pong p: w.F64(p.At); break;
            case DeviceInfo d: w.Str(d.Id); w.Str(d.Name); w.Str(d.Os); break;
        }
        var body = w.ToArray();
        var framed = new byte[4 + body.Length];
        BigEndian(framed, 0, (uint)body.Length);
        body.CopyTo(framed, 4);
        return framed;
    }

    /// Parses one frame body (type byte onward). Anything malformed throws, and the link closes.
    public static Frame Decode(byte[] body)
    {
        var r = new Reader(body);
        Frame f = (FrameType)r.U8() switch
        {
            FrameType.Hello => new Hello(r.Int(), r.Str(), r.Str(), r.Str(), r.Str(), r.F64()),
            FrameType.Summary => new Summary(r.Many(13, r.Stamp)),
            FrameType.Put => r.Entry(),
            FrameType.Tombstone => new Tombstone(r.Stamp()),
            FrameType.Fetch => new Fetch(r.Str(), r.Int(), r.I64()),
            FrameType.Chunk => new Chunk(r.Str(), r.Int(), r.I64(), r.I64(), r.Bytes()),
            FrameType.Cancel => new Cancel(r.Str()),
            FrameType.Applied => new Applied(r.Str()),
            FrameType.Offer => new Offer(r.Str()),
            FrameType.Ping => new Ping(r.F64()),
            FrameType.Pong => new Pong(r.F64()),
            FrameType.Device => new DeviceInfo(r.Str(), r.Str(), r.Str()),
            _ => throw new Malformed(),
        };
        if (r.Remaining != 0) throw new Malformed();
        return f;
    }

    static void BigEndian(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    sealed class Writer
    {
        readonly MemoryStream s = new();
        public void U8(byte v) => s.WriteByte(v);
        public void U32(uint v) { var b = new byte[4]; BigEndian(b, 0, v); s.Write(b, 0, 4); }
        public void I64(long v) { U32((uint)((ulong)v >> 32)); U32((uint)v); }
        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));
        public void Bytes(byte[] b) { U32((uint)b.Length); s.Write(b, 0, b.Length); }
        public void Str(string v) => Bytes(Encoding.UTF8.GetBytes(v));
        public void Stamp(Stamp st) { Str(st.Hash); F64(st.Time); U8(st.Deleted ? (byte)1 : (byte)0); }
        public byte[] ToArray() => s.ToArray();
    }

    sealed class Reader(byte[] d)
    {
        int at;
        public int Remaining => d.Length - at;

        int Take(long n)
        {
            if (n < 0 || n > Remaining) throw new Malformed();
            var start = at;
            at += (int)n;
            return start;
        }

        public byte U8() => d[Take(1)];
        public uint U32() { var i = Take(4); return (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]); }
        public int Int() => U32() is var v && v <= int.MaxValue ? (int)v : throw new Malformed();
        public long I64() => (long)((ulong)U32() << 32 | U32());

        public double F64()
        {
            var v = BitConverter.Int64BitsToDouble(I64());
            return double.IsNaN(v) || double.IsInfinity(v) ? throw new Malformed() : v;
        }

        public byte[] Bytes()
        {
            var n = U32();
            var i = Take(n);
            var b = new byte[n];
            Array.Copy(d, i, b, 0, n);
            return b;
        }

        public string Str()
        {
            try { return Utf8.GetString(Bytes()); }
            catch (DecoderFallbackException) { throw new Malformed(); }
        }

        public Stamp Stamp()
        {
            var (h, t, del) = (Str(), F64(), U8());
            return del > 1 ? throw new Malformed() : new(h, t, del == 1);
        }

        /// A count can't promise more items than the bytes left could hold.
        public List<T> Many<T>(int minSize, Func<T> one)
        {
            var n = U32();
            if (n > Remaining / minSize) throw new Malformed();
            var list = new List<T>((int)n);
            for (var i = 0; i < n; i++) list.Add(one());
            return list;
        }

        public Entry Entry()
        {
            var (h, t, kind, text, source, origin) = (Str(), F64(), Str(), Str(), Str(), Str());
            var blobs = Many(16, () => new Blob(Str(), I64(), Str()));
            if (kind is not ("text" or "image" or "file") || blobs.Any(b => b.Size < 0)) throw new Malformed();
            return new Entry(h, t, kind, text, source, origin, blobs);
        }
    }
}

/// Splits a byte stream into frames. Rejects an oversized length before buffering it.
sealed class FrameReader
{
    byte[] buf = new byte[0];
    int len;

    public List<Frame> Feed(byte[] data, int count)
    {
        if (len + count > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + count));
        Array.Copy(data, 0, buf, len, count);
        len += count;
        var output = new List<Frame>();
        var at = 0;
        while (len - at >= 4)
        {
            var n = (uint)(buf[at] << 24 | buf[at + 1] << 16 | buf[at + 2] << 8 | buf[at + 3]);
            if (n == 0 || n > SyncProto.MaxFrame) throw new Malformed();
            if (len - at - 4 < n) break;
            var body = new byte[n];
            Array.Copy(buf, at + 4, body, 0, n);
            output.Add(Codec.Decode(body));
            at += 4 + (int)n;
        }
        Array.Copy(buf, at, buf, 0, len - at);
        len -= at;
        return output;
    }
}

enum MergeAction { Ignore, Insert, Bump, Delete }

static class SyncRules
{
    /// What an incoming event does here.
    public static MergeAction Merge(Stamp? local, bool hasRow, Stamp incoming) =>
        !incoming.Beats(local) ? MergeAction.Ignore : incoming.Deleted ? MergeAction.Delete : hasRow ? MergeAction.Bump : MergeAction.Insert;

    /// The local stamps that beat the peer's: what this side sends after reading the peer's summary.
    public static List<Stamp> Reconcile(IEnumerable<Stamp> local, IEnumerable<Stamp> remote)
    {
        var theirs = new Dictionary<string, Stamp>();
        foreach (var s in remote)
            if (s.Beats(theirs.TryGetValue(s.Hash, out var t) ? t : null)) theirs[s.Hash] = s;
        return local.Where(s => s.Beats(theirs.TryGetValue(s.Hash, out var t) ? t : null)).ToList();
    }
}
