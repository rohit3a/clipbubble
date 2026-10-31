import time
import unittest

try:
    from gi.repository import GLib
except ImportError:
    GLib = None

from clipbubble import board
from clipbubble.store import DB, IMAGES, Prefs, sha


class FakeShell:
    def __init__(self):
        self.board = {}
        self.window = {"wmClass": "SomeEditor"}
        self.on_clipboard = None

    def subscribe(self, on_clipboard, on_hotkey=None):
        self.on_clipboard = on_clipboard

    def read(self, mime):
        return self.board.get(mime)

    def focused(self):
        return self.window

    def set(self, mime, data):
        self.board = {mime: data}
        return True

    def copy(self, items, ours=False):
        self.board = items
        self.on_clipboard(list(items), ours)


@unittest.skipIf(GLib is None, "needs PyGObject (runs on Linux)")
class CaptureRules(unittest.TestCase):
    def settle(self):
        ctx = GLib.MainContext.default()
        end = time.time() + 0.9
        while time.time() < end:
            ctx.iteration(False)
            time.sleep(0.01)

    def setUp(self):
        from clipbubble.capture import Capture
        self.shell = FakeShell()
        self.cap = Capture(self.shell)
        self.db = DB()

    def test_text_is_saved_with_its_app(self):
        t = f"hello {time.time()}"
        self.shell.copy({"text/plain;charset=utf-8": t.encode()})
        self.settle()
        c = self.db.clip(hash="t:" + sha(t))
        self.assertIsNotNone(c)
        self.assertEqual(c.source, "SomeEditor")

    def test_secrets_and_our_own_writes_are_skipped(self):
        secret = f"hunter2 {time.time()}"
        self.shell.copy({"text/plain;charset=utf-8": secret.encode(), "x-kde-passwordManagerHint": b"secret"})
        self.settle()
        self.assertFalse(self.db.exists("t:" + sha(secret)))
        synced = f"synced {time.time()}"
        self.shell.copy({"text/plain;charset=utf-8": synced.encode()}, ours=True)
        self.settle()
        self.assertFalse(self.db.exists("t:" + sha(synced)))

    def test_copying_the_last_item_again_after_a_synced_one_counts(self):
        t = f"mine {time.time()}"
        self.shell.copy({"text/plain;charset=utf-8": t.encode()})
        self.settle()
        before = self.db.stamp("t:" + sha(t)).time
        self.shell.copy({"text/plain;charset=utf-8": b"from the iPhone"}, ours=True)
        self.settle()
        self.shell.copy({"text/plain;charset=utf-8": t.encode()})
        self.settle()
        self.assertGreater(self.db.stamp("t:" + sha(t)).time, before)

    def test_an_image_is_written_only_with_copied_images_on(self):
        png = b"\x89PNG\r\n\x1a\n" + str(time.time()).encode()
        Prefs.set("save.images", False)
        self.addCleanup(Prefs.set, "save.images", True)
        self.shell.copy({"image/png": png})
        self.settle()
        self.assertFalse((IMAGES / (sha(png) + ".png")).exists())

    def test_files_and_images(self):
        self.shell.copy({"x-special/gnome-copied-files": b"copy\nfile:///tmp/a%20b.txt\nfile:///tmp/c.pdf"})
        self.settle()
        self.assertTrue(self.db.exists("f:" + sha("/tmp/a b.txt\n/tmp/c.pdf")))
        png = b"\x89PNG\r\n\x1a\n" + str(time.time()).encode()
        self.shell.copy({"image/png": png})
        self.settle()
        self.assertTrue(self.db.exists("i:" + sha(png)))

    def test_paste_through_is_dropped(self):
        a, b = f"kept {time.time()}", f"flash {time.time()}"
        self.shell.copy({"text/plain": a.encode()})
        self.settle()
        self.shell.copy({"text/plain": b.encode()})
        self.settle()
        self.shell.copy({"text/plain": a.encode()})
        self.settle()
        self.assertTrue(self.db.exists("t:" + sha(a)))
        self.assertFalse(self.db.exists("t:" + sha(b)), "a copy that's restored within 2.5 s was a paste-through")


class Writes(unittest.TestCase):
    def test_board_write_kinds(self):
        from clipbubble.store import Clip
        s = FakeShell()
        self.assertTrue(board.write(s, Clip(0, "text", "hi", None, "", 0, "t:x", "", False)))
        self.assertEqual(s.board, {"text/plain;charset=utf-8": b"hi"})
        self.assertFalse(board.write(s, Clip(0, "file", "/nope/missing", None, "", 0, "f:x", "", False)))
