import json
import random
import unittest
from pathlib import Path

from clipbubble import sync
from clipbubble.sync import Blob, Entry, Hello, Stamp

FIXTURES = Path(__file__).resolve().parents[2] / "Tests/fixtures/sync/frames.json"

FRAMES = {
    "hello": (sync.HELLO, Hello(1, "3F2A-mac", "Rohit's MacBook", "macos", "1.4.0", 1792000000.25)),
    "summary": (sync.SUMMARY, [Stamp("t:abc", 1792000000.5, False), Stamp("i:def", 1791999999, True)]),
    "summary-empty": (sync.SUMMARY, []),
    "put-text": (sync.PUT, Entry("t:01", 1792000001, "text", "héllo\nwörld 👋", "Safari", "3F2A-mac")),
    "put-image": (sync.PUT, Entry("i:ff", 1792000002, "image", "", "Figma", "rog", (Blob("ff.png", 1234, "ff"),))),
    "put-files": (sync.PUT, Entry("f:9", 1792000003, "file", "a.pdf\nb c.zip", "Finder", "3F2A-mac",
                                  (Blob("a.pdf", 10, ""), Blob("b c.zip", 30_000_000_000, "")))),
    "tombstone": (sync.TOMBSTONE, Stamp("t:01", 1792000004, True)),
    "fetch": (sync.FETCH, ("f:9", 1, 262144)),
    "chunk": (sync.CHUNK, ("f:9", 0, 0, 10, bytes([0, 1, 2, 255, 254, 10, 13, 0, 7, 9]))),
    "cancel": (sync.CANCEL, "f:9"),
    "applied": (sync.APPLIED, "t:01"),
    "ping": (sync.PING, 1792000005.125),
    "pong": (sync.PONG, 1792000005.125),
}


class Codec(unittest.TestCase):
    def test_matches_swift_bytes(self):
        committed = {r["name"]: r["hex"] for r in json.loads(FIXTURES.read_text())}
        self.assertEqual(set(committed), set(FRAMES))
        for name, frame in FRAMES.items():
            self.assertEqual(sync.encode(frame).hex(), committed[name], name)
            self.assertEqual(sync.FrameReader().feed(bytes.fromhex(committed[name])), [frame], name)

    def test_split_reads(self):
        data = b"".join(sync.encode(f) for f in FRAMES.values())
        r, got = sync.FrameReader(), []
        for i in range(0, len(data), 7):
            got += r.feed(data[i:i + 7])
        self.assertEqual(got, list(FRAMES.values()))

    def test_rejects_bad_input(self):
        with self.assertRaises(sync.Malformed):
            sync.FrameReader().feed(b"\x7f\xff\xff\xff")
        with self.assertRaises(sync.Malformed):
            sync.FrameReader().feed(b"\0\0\0\0")
        body = sync.encode((sync.PING, 1.0))[4:]
        for bad in (body[:-1], body + b"\0", b"\x63", b"\x09" + bytes.fromhex("7ff8000000000000"),
                    b"\x02\xff\xff\xff\xff"):
            with self.assertRaises(sync.Malformed):
                sync.decode(bad)

    def test_fuzz_never_crashes(self):
        rnd = random.Random(7)
        seeds = [sync.encode(f)[4:] for f in FRAMES.values()]
        for _ in range(3000):
            b = bytearray(rnd.choice(seeds))
            for _ in range(rnd.randint(1, 4)):
                if b:
                    b[rnd.randrange(len(b))] = rnd.randrange(256)
            try:
                sync.decode(bytes(b))
            except sync.Malformed:
                pass


class Merge(unittest.TestCase):
    def test_rules(self):
        a = Stamp("t:x", 10, False)
        self.assertEqual(sync.merge(None, False, a), "insert")
        self.assertEqual(sync.merge(Stamp("t:x", 5, False), True, a), "bump")
        self.assertEqual(sync.merge(Stamp("t:x", 20, False), True, a), "ignore")
        self.assertEqual(sync.merge(a, True, Stamp("t:x", 10, True)), "delete")
        self.assertEqual(sync.merge(Stamp("t:x", 10, True), False, a), "ignore")

    def test_order_independent(self):
        rnd = random.Random(3)
        for _ in range(300):
            events = [Stamp(rnd.choice("abc"), rnd.randint(0, 5), rnd.random() < 0.4) for _ in range(8)]
            results = set()
            for _ in range(4):
                rnd.shuffle(events)
                state = {}
                for e in events:
                    if sync.merge(state.get(e.hash), e.hash in state, e) != "ignore":
                        state[e.hash] = e
                results.add(tuple(sorted(state.items())))
            self.assertEqual(len(results), 1)

    def test_reconcile(self):
        mine = [Stamp("a", 5, False), Stamp("b", 5, False), Stamp("c", 5, True)]
        theirs = [Stamp("a", 6, False), Stamp("b", 5, False)]
        self.assertEqual(sync.reconcile(mine, theirs), [Stamp("c", 5, True)])
