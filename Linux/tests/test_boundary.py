"""docs/sync-protocol.md: a device reaches only files ClipBubble offered it from history."""
import os
import socket
import threading
import time
import unittest
from pathlib import Path

from clipbubble import engine as eng
from clipbubble import sync
from clipbubble.store import DB, Prefs, sha
from clipbubble.sync import Blob, Entry, Hello

from . import HOME


class RawPeer:
    """Plays a linked device by hand, so a test can send what no honest engine would."""

    def __init__(self, e, device="raw-peer", summary=()):
        ours, theirs = socket.socketpair()
        self.sock = ours
        self.frames = []
        self.lock = threading.Lock()
        self.reader = sync.FrameReader()
        threading.Thread(target=self._read, daemon=True).start()
        self.engine = e
        fd = theirs.detach()
        self.session = eng.Session(fd, fd, e)
        e.post(lambda: e.begin(self.session))
        self.send((sync.HELLO, Hello(sync.PROTO, device, "Lost iPhone", "ios", "1.6.2", time.time())))
        self.send((sync.SUMMARY, list(summary)))
        self.first(lambda f: f[0] == sync.SUMMARY)

    def _read(self):
        while True:
            try:
                d = self.sock.recv(1 << 20)
            except OSError:
                return
            if not d:
                return
            with self.lock:
                self.frames += self.reader.feed(d)

    def send(self, frame):
        self.sock.sendall(sync.encode(frame))

    def first(self, match, seconds=8):
        end = time.time() + seconds
        while time.time() < end:
            with self.lock:
                hit = next((f for f in self.frames if match(f)), None)
            if hit:
                return hit
            time.sleep(0.01)
        return None

    def put(self, h):
        f = self.first(lambda f: f[0] == sync.PUT and f[1].hash == h)
        return f[1] if f else None

    def fetch(self, h, index=0, offset=0):
        """The bytes of a chunk, or None for a cancel."""
        self.send((sync.FETCH, (h, index, offset)))
        reply = self.first(lambda f: (f[0] == sync.CHUNK and f[1][0] == h) or (f[0] == sync.CANCEL and f[1] == h))
        assert reply, f"the engine answers a fetch of {h}"
        with self.lock:
            self.frames.remove(reply)
        return reply[1][4] if reply[0] == sync.CHUNK else None

    def close(self):
        self.sock.close()


class Boundary(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.db = DB()
        cls.engine = eng.Engine()
        cls.applied = []
        cls.engine.apply = lambda c: cls.applied.append(c.hash)

    def setUp(self):
        self.home = Path(HOME) / "boundary"
        self.home.mkdir(exist_ok=True)
        self.secret = self.home / "secret.txt"
        self.secret.write_bytes(b"not for peers")
        self.cwd = os.getcwd()
        os.chdir(self.home)
        self.addCleanup(os.chdir, self.cwd)

    def test_a_peers_text_is_never_a_path(self):
        link = self.home / "innocent.txt"
        link.unlink(missing_ok=True)
        link.symlink_to(self.secret)
        s = str(self.secret)
        peer = RawPeer(self.engine)
        self.addCleanup(peer.close)
        names = [s, f"../{self.home.name}/secret.txt", "./secret.txt", "secret.txt", "innocent.txt", str(link), "~/secret.txt",
                 "file://" + s, "//" + s, "/" + s, " " + s, "safe.txt\n" + s, "\\" + s, s + "\r"]
        for i, text in enumerate(names):
            h = f"f:probe{i}"
            peer.send((sync.PUT, Entry(h, time.time(), "file", text, "Finder", self.engine.device)))
            self.assertIsNone(peer.fetch(h), text)
            self.assertIsNone(peer.fetch(h, index=1), text)
            self.assertTrue(all("/" not in p for p in self.db.clip(hash=h).paths), text)
        self.assertEqual(self.applied, [], "a file item with no bytes never lands on the clipboard")

    def test_a_row_waiting_for_bytes_keeps_a_bare_name(self):
        peer = RawPeer(self.engine)
        self.addCleanup(peer.close)
        peer.send((sync.PUT, Entry("f:waiting", time.time(), "file", str(self.secret), "Finder", "elsewhere",
                                   (Blob("../../secret.txt", 5, ""),))))
        self.assertIsNone(peer.fetch("f:waiting"))
        self.assertEqual(self.db.clip(hash="f:waiting").text, eng.safe_file_name(str(self.secret)))
        peer.send((sync.CANCEL, "f:waiting"))

    def test_only_what_this_link_was_offered_comes_over(self):
        mine = self.home / "mine.txt"
        mine.write_bytes(b"offered")
        h = "f:" + sha(str(mine))
        self.db.upsert("file", str(mine), None, "Finder", h)

        liar = RawPeer(self.engine, "liar", [self.db.stamp(h)])
        self.addCleanup(liar.close)
        self.assertIsNone(liar.fetch(h), "claiming to know an item in a summary doesn't offer it")
        self.assertIsNone(liar.fetch("f:" + sha(str(self.secret))), "the hash of something never in history gets nothing")

        honest = RawPeer(self.engine, "honest")
        self.addCleanup(honest.close)
        self.assertEqual(honest.put(h).text, "mine.txt", "offered by name, not by path")
        self.assertEqual(honest.fetch(h), b"offered")
        self.assertIsNone(honest.fetch(h, offset=-1))
        self.assertIsNone(honest.fetch(h, offset=len(b"offered") + 1))
        self.assertIsNone(honest.fetch(h, index=1 << 31))
        self.db.delete(hash=h)
        self.assertIsNone(honest.fetch(h), "a delete takes it back at once")

    def test_a_category_switched_off_stops_being_offered(self):
        shot = self.home / "shot.txt"
        shot.write_bytes(b"hidden")
        h = "f:" + sha(str(shot))
        self.db.upsert("file", str(shot), None, "Finder", h)
        early = RawPeer(self.engine, "early")
        self.addCleanup(early.close)
        self.assertIsNotNone(early.put(h))
        Prefs.set("save.files", False)
        self.addCleanup(Prefs.set, "save.files", True)
        self.assertIsNone(early.fetch(h), "switching a category off takes back what was offered")
        hidden = RawPeer(self.engine, "hidden")
        self.addCleanup(hidden.close)
        self.assertIsNone(hidden.fetch(h), "and it isn't offered to the next device")


if __name__ == "__main__":
    unittest.main()
