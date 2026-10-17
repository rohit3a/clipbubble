"""The Linux engine against the Mac engine in a real process: the same protocol from both codebases."""
import os
import subprocess
import time
import unittest
from pathlib import Path

from clipbubble import engine as eng
from clipbubble.store import DB, IMAGES, sha
from clipbubble.sync import Blob, Entry

from . import HOME

SWIFT = Path(__file__).resolve().parents[2] / ".build/tests"


def wait_for(cond, seconds=10):
    end = time.time() + seconds
    while time.time() < end:
        if cond():
            return True
        time.sleep(0.05)
    return False


@unittest.skipUnless(SWIFT.exists(), "build the Swift tests first (./tests.sh)")
class SwiftPeer(unittest.TestCase):
    def test_sync_with_mac_engine(self):
        peer_home = Path(HOME) / "swift-peer"
        peer_home.mkdir(parents=True, exist_ok=True)
        big = Path(HOME) / "big file.bin"
        big_data = bytes((i * 31) & 0xFF for i in range(600_000))
        big.write_bytes(big_data)
        live_there = f"mac live {time.time()}"
        huge, huge_here = Path(HOME) / "huge.bin", Path(HOME) / "huge here.bin"
        huge_data = os.urandom(26 << 20)
        huge.write_bytes(huge_data)
        huge_here.write_bytes(huge_data[::-1])
        env = dict(os.environ, CLIPBUBBLE_HOME=str(peer_home), CLIPBUBBLE_DOWNLOADS=str(peer_home / "Downloads"),
                   CLIPBUBBLE_DEVICE_NAME="Mac", CLIPBUBBLE_CHILD="serve", SEED_TEXT="mac-old", SEED_FILE=str(big), SEED_BIG=str(huge),
                   LIVE_TEXT=live_there)

        db = DB()
        db.upsert("text", "linux-old", None, "Terminal", "t:" + sha("linux-old"))
        # Left from a link that dropped mid-download: the row is here, the bytes aren't.
        db.insert(Entry("f:" + sha(str(big)), time.time() - 1, "file", "big file.bin", "Files", "mac-device",
                        (Blob("big file.bin", len(big_data), ""),)), "mac-device")
        db.upsert("file", str(huge_here), None, "Files", "f:" + sha(str(huge_here)))
        e = eng.Engine()
        seen = []
        e.on_transfers = lambda t: seen.extend(t.items())
        applied = []
        e.apply = lambda c: applied.append(c.hash)

        p = subprocess.Popen([str(SWIFT)], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        s = eng.Session(os.dup(p.stdout.fileno()), os.dup(p.stdin.fileno()), e)
        p.stdout.close()
        p.stdin.close()
        e.post(lambda: e.begin(s))

        mac_text, file_hash = "t:" + sha("mac-old"), "f:" + sha(str(big))
        self.assertTrue(wait_for(lambda: db.exists(mac_text)), "the Mac's history arrives")
        self.assertTrue(db.clip(hash=mac_text).origin, "marked as from the Mac")
        self.assertIn("Mac", db.device_names().values())
        self.assertTrue(wait_for(lambda: (c := db.clip(hash=file_hash)) is not None and not c.remote), "a download cut off by a dropped link finishes on the next one")
        landed = db.clip(hash=file_hash).paths[0]
        self.assertEqual(Path(landed).read_bytes(), big_data)
        self.assertTrue(landed.endswith("Downloads/ClipBubble/Mac/big file.bin"))
        self.assertTrue(wait_for(lambda: "t:" + sha(live_there) in applied), "a fresh copy on the Mac lands here")
        self.assertNotIn(mac_text, applied, "old history doesn't touch the clipboard")

        applied_there = peer_home / "applied.txt"
        live = f"linux live {time.time()}"
        db.upsert("text", live, None, "Terminal", "t:" + sha(live))
        self.assertTrue(wait_for(lambda: applied_there.exists() and "t:" + sha(live) in applied_there.read_text()),
                        "a fresh copy here lands on the Mac")

        png = b"\x89PNG\r\n\x1a\n" + os.urandom(3000)
        img = IMAGES / (sha(png) + ".png")
        img.write_bytes(png)
        db.upsert("image", "", str(img), "Screenshot", "i:" + sha(png))
        self.assertTrue(wait_for(lambda: "i:" + sha(png) in applied_there.read_text()), "an image lands on the Mac")

        # Over 25 MB waits for Get, and resumes a partial file a dropped link left behind.
        hh = "f:" + sha(str(huge))
        self.assertTrue(wait_for(lambda: db.exists(hh)))
        time.sleep(0.5)
        self.assertTrue(db.clip(hash=hh).remote, "a file over 25 MB doesn't download on its own")
        partial = eng.DIR / "incoming" / f"{sha(hh)}-0"
        partial.parent.mkdir(parents=True, exist_ok=True)
        head = bytes([huge_data[0] ^ 0xFF]) + huge_data[1:5 << 20]
        partial.write_bytes(head)
        e.get(hh)
        self.assertTrue(wait_for(lambda: not db.clip(hash=hh).remote, 30), "Get downloads it")
        got = Path(db.clip(hash=hh).paths[0]).read_bytes()
        self.assertEqual((got[:1], got[1:]), (head[:1], huge_data[1:]), "it resumed from the partial file")
        self.assertTrue(any(h == hh and not t.sending and t.total == 26 << 20 for h, t in seen), "progress is reported")

        # Send to: this side asks the Mac to take a big file now.
        e.send_to("f:" + sha(str(huge_here)), (peer_home / "device-id").read_text().strip())
        there = peer_home / "Downloads/ClipBubble/Linux test/huge here.bin"
        self.assertTrue(wait_for(lambda: there.exists() and there.stat().st_size == len(huge_data), 30), "Send to puts it on the Mac")
        self.assertTrue(any(t.sending for _, t in seen), "sending shows progress")

        db.delete(hash="t:" + sha(live))
        time.sleep(0.7)
        e.post(s.close)
        p.wait(timeout=10)

        env["CLIPBUBBLE_CHILD"] = "dump"
        lines = subprocess.run([str(SWIFT)], env=env, capture_output=True, text=True, timeout=10).stdout.splitlines()
        self.assertTrue(any(l.startswith("clip|t:" + sha("linux-old") + "|") for l in lines), "the Mac got this history")
        self.assertIn("deleted|t:" + sha(live), lines, "the delete reached the Mac")
        self.assertTrue(any(l.startswith("clip|i:" + sha(png) + "|") and l.endswith("|false|true") for l in lines),
                        "the image is stored on the Mac")
