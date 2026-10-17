"""The device-sync model and wire format shared with the Mac and Windows apps (docs/sync-protocol.md)."""
import math
import struct
from dataclasses import dataclass, field

PROTO = 1
MAX_FRAME = 4 << 20
CHUNK_SIZE = 256 << 10
AUTO_FETCH_LIMIT = 25 << 20


class Malformed(Exception):
    pass


@dataclass(frozen=True)
class Stamp:
    hash: str
    time: float
    deleted: bool

    def beats(self, other):
        """Later wins. On a tie a delete wins, so taking something back never loses to a race."""
        if other is None:
            return True
        return self.time > other.time or (self.time == other.time and self.deleted and not other.deleted)


@dataclass(frozen=True)
class Blob:
    name: str
    size: int
    sha: str


@dataclass(frozen=True)
class Entry:
    hash: str
    time: float
    kind: str
    text: str
    source: str
    origin: str
    blobs: tuple = ()

    @property
    def stamp(self):
        return Stamp(self.hash, self.time, False)

    @property
    def size(self):
        return sum(b.size for b in self.blobs)


@dataclass(frozen=True)
class Hello:
    proto: int
    device: str
    name: str
    os: str
    version: str
    clock: float


# Frames are (name, fields...) tuples, with the same names as the Swift enum.
HELLO, SUMMARY, PUT, TOMBSTONE, FETCH, CHUNK, CANCEL, APPLIED, PING, PONG = range(1, 11)
NAMES = {1: "hello", 2: "summary", 3: "put", 4: "tombstone", 5: "fetch", 6: "chunk",
         7: "cancel", 8: "applied", 9: "ping", 10: "pong"}


def is_bulk(frame):
    return frame[0] == CHUNK


class _W:
    def __init__(self):
        self.parts = []

    def u8(self, v): self.parts.append(struct.pack(">B", v))
    def u32(self, v): self.parts.append(struct.pack(">I", v))
    def i64(self, v): self.parts.append(struct.pack(">q", v))
    def f64(self, v): self.parts.append(struct.pack(">d", v))

    def bytes(self, b):
        self.u32(len(b))
        self.parts.append(bytes(b))

    def str(self, s): self.bytes(s.encode("utf-8"))

    def stamp(self, s):
        self.str(s.hash)
        self.f64(s.time)
        self.u8(1 if s.deleted else 0)


def encode(frame):
    """frame is (code, payload): hello Hello, summary [Stamp], put Entry, tombstone Stamp,
    fetch (hash, index, offset), chunk (hash, index, offset, total, data), cancel/applied hash, ping/pong float."""
    code, p = frame
    w = _W()
    w.u8(code)
    if code == HELLO:
        w.u32(p.proto); w.str(p.device); w.str(p.name); w.str(p.os); w.str(p.version); w.f64(p.clock)
    elif code == SUMMARY:
        w.u32(len(p))
        for s in p:
            w.stamp(s)
    elif code == PUT:
        w.str(p.hash); w.f64(p.time); w.str(p.kind); w.str(p.text); w.str(p.source); w.str(p.origin)
        w.u32(len(p.blobs))
        for b in p.blobs:
            w.str(b.name); w.i64(b.size); w.str(b.sha)
    elif code == TOMBSTONE:
        w.stamp(p)
    elif code == FETCH:
        w.str(p[0]); w.u32(p[1]); w.i64(p[2])
    elif code == CHUNK:
        w.str(p[0]); w.u32(p[1]); w.i64(p[2]); w.i64(p[3]); w.bytes(p[4])
    elif code in (CANCEL, APPLIED):
        w.str(p)
    elif code in (PING, PONG):
        w.f64(p)
    else:
        raise ValueError(code)
    body = b"".join(w.parts)
    return struct.pack(">I", len(body)) + body


class _R:
    def __init__(self, data):
        self.d = memoryview(data)
        self.at = 0

    @property
    def remaining(self):
        return len(self.d) - self.at

    def take(self, n):
        if n < 0 or n > self.remaining:
            raise Malformed()
        b = self.d[self.at:self.at + n]
        self.at += n
        return b

    def u8(self): return self.take(1)[0]
    def u32(self): return struct.unpack(">I", self.take(4))[0]
    def i64(self): return struct.unpack(">q", self.take(8))[0]

    def f64(self):
        v = struct.unpack(">d", self.take(8))[0]
        if not math.isfinite(v):
            raise Malformed()
        return v

    def bytes(self): return bytes(self.take(self.u32()))

    def str(self):
        try:
            return self.bytes().decode("utf-8")
        except UnicodeDecodeError:
            raise Malformed()

    def stamp(self):
        h, t, d = self.str(), self.f64(), self.u8()
        if d > 1:
            raise Malformed()
        return Stamp(h, t, d == 1)


def decode(body):
    """Parses one frame body (type byte onward). Anything malformed raises, and the link closes."""
    r = _R(body)
    code = r.u8()
    if code == HELLO:
        p = Hello(r.u32(), r.str(), r.str(), r.str(), r.str(), r.f64())
    elif code == SUMMARY:
        n = r.u32()
        if n > r.remaining // 13:
            raise Malformed()
        p = [r.stamp() for _ in range(n)]
    elif code == PUT:
        h, t, kind, text, source, origin = r.str(), r.f64(), r.str(), r.str(), r.str(), r.str()
        n = r.u32()
        if n > r.remaining // 16:
            raise Malformed()
        blobs = tuple(Blob(r.str(), r.i64(), r.str()) for _ in range(n))
        if kind not in ("text", "image", "file") or any(b.size < 0 for b in blobs):
            raise Malformed()
        p = Entry(h, t, kind, text, source, origin, blobs)
    elif code == TOMBSTONE:
        p = r.stamp()
    elif code == FETCH:
        p = (r.str(), r.u32(), r.i64())
    elif code == CHUNK:
        p = (r.str(), r.u32(), r.i64(), r.i64(), r.bytes())
    elif code in (CANCEL, APPLIED):
        p = r.str()
    elif code in (PING, PONG):
        p = r.f64()
    else:
        raise Malformed()
    if r.remaining:
        raise Malformed()
    return (code, p)


class FrameReader:
    """Splits a byte stream into frames. Rejects an oversized length before buffering it."""

    def __init__(self):
        self.buf = bytearray()

    def feed(self, data):
        self.buf += data
        out = []
        while len(self.buf) >= 4:
            n = struct.unpack(">I", self.buf[:4])[0]
            if n == 0 or n > MAX_FRAME:
                raise Malformed("frame length")
            if len(self.buf) < 4 + n:
                break
            out.append(decode(bytes(self.buf[4:4 + n])))
            del self.buf[:4 + n]
        return out


def merge(local, has_row, incoming):
    """What an incoming event does here: 'ignore', 'insert', 'bump' or 'delete'."""
    if not incoming.beats(local):
        return "ignore"
    if incoming.deleted:
        return "delete"
    return "bump" if has_row else "insert"


def reconcile(local, remote):
    """The local stamps that beat the peer's: what this side sends after reading the peer's summary."""
    theirs = {}
    for s in remote:
        if s.beats(theirs.get(s.hash)):
            theirs[s.hash] = s
    return [s for s in local if s.beats(theirs.get(s.hash))]
