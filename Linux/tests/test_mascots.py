import unittest

from clipbubble import mascots
from clipbubble.mcp import Server

try:
    import cairo  # noqa: F401
    HAS_CAIRO = True
except ImportError:
    HAS_CAIRO = False


class Mascots(unittest.TestCase):
    def tool(self, tool, **a):
        r = Server().call(tool, a)
        return r["content"][0]["text"], r.get("isError", False)

    def test_add_set_remove_through_mcp(self):
        out, err = self.tool("mascot_add", name="Pink Pal", kind="blob", color="#FF6AD5")
        self.assertFalse(err, out)
        self.assertEqual(mascots.active_id(), "pink-pal")
        out, _ = self.tool("mascot_list")
        self.assertIn("pink-pal · Pink Pal · blob · custom · #FF6AD5  ← active", out)
        self.assertTrue(self.tool("mascot_add", name="Bad", kind="blob", color="pink")[1], "a blob needs a hex color")
        self.assertTrue(self.tool("mascot_set", id="nope")[1])
        self.assertFalse(self.tool("mascot_set", id="cat")[1])
        self.assertEqual(mascots.active_id(), "cat")
        self.assertTrue(self.tool("mascot_remove", id="blob")[1], "built-ins stay")
        self.tool("mascot_set", id="pink-pal")
        self.assertFalse(self.tool("mascot_remove", id="pink-pal")[1])
        self.assertEqual(mascots.active_id(), "blob", "removing the active one falls back to Blob")

    def test_image_mascot_from_svg(self):
        svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="5"/></svg>'
        m = mascots.add("Dot", "image", states={"idle": svg})
        self.assertEqual(mascots.frames(m), {"idle": str(mascots.FOLDER / "dot-idle.svg")})
        self.assertTrue(mascots.remove("dot"))
        self.assertFalse((mascots.FOLDER / "dot-idle.svg").exists())

    @unittest.skipUnless(HAS_CAIRO, "needs pycairo")
    def test_blob_frames_are_drawn_once(self):
        f = mascots.frames(mascots.BUILTINS[0])
        self.assertEqual(set(f), {"idle", "hover", "active", "blink"})
        self.assertEqual(open(f["idle"], "rb").read(8), b"\x89PNG\r\n\x1a\n")
        self.assertEqual(mascots.frames(mascots.BUILTINS[0]), f)
