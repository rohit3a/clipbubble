import time
import unittest

from clipbubble import store
from clipbubble.store import DB, Prefs, sha
from clipbubble.sync import Entry, Stamp


class Store(unittest.TestCase):
    def setUp(self):
        self.db = DB()
        self.db.c.execute("DELETE FROM clips")
        self.db.c.execute("DELETE FROM forgotten")

    def test_delete_leaves_tombstone(self):
        self.db.upsert("text", "x", None, "T", "t:" + sha("x"))
        self.db.delete(hash="t:" + sha("x"))
        st = self.db.stamp("t:" + sha("x"))
        self.assertTrue(st.deleted)
        old = time.time() - 60
        self.db.insert_if_missing("text", "x", "T", "t:" + sha("x"), old)
        self.assertFalse(self.db.exists("t:" + sha("x")), "a deleted item doesn't come back with an older time")

    def test_sync_insert_bump_tombstone(self):
        now = time.time()
        e = Entry("t:y", now - 5, "text", "y", "Safari", "dev-1")
        self.db.insert(e, "dev-1")
        c = self.db.clip(hash="t:y")
        self.assertEqual((c.origin, c.remote), ("dev-1", False))
        self.db.bump("t:y", now)
        self.assertAlmostEqual(self.db.stamp("t:y").time, now)
        self.db.tombstone(Stamp("t:y", now - 1, True))
        self.assertTrue(self.db.exists("t:y"), "an older delete loses to a newer copy")
        self.db.tombstone(Stamp("t:y", now + 1, True))
        self.assertFalse(self.db.exists("t:y"))

    def test_hidden_categories(self):
        self.assertTrue(self.db.allowed("text", "Safari"))
        Prefs.set("save.text", False)
        try:
            self.assertFalse(self.db.allowed("text", "Safari"))
            self.db.upsert("text", "z", None, "T", "t:z")
            self.assertNotIn("t:z", [s.hash for s in self.db.stamps()], "hidden items aren't offered to peers")
        finally:
            Prefs.set("save.text", True)

    def test_changed_hook(self):
        seen, hook = [], store.changed
        store.changed = seen.extend
        try:
            self.db.upsert("text", "w", None, "T", "t:w")
        finally:
            store.changed = hook
        self.assertEqual(seen, ["t:w"])

    def test_devices_filter_source(self):
        self.db.save_device("dev-2", "ROG", "linux")
        self.db.insert(Entry("t:r", time.time(), "text", "r", "Terminal", "dev-2"), "dev-2")
        self.assertEqual([c.hash for c in self.db.clips(source="rog")], ["t:r"])
