"""Stdio MCP server (clipbubble --mcp) so agents on this machine can read and set the clipboard history."""
import base64
import json
import sys
import time

from .engine import VERSION
from .store import DB

INSTRUCTIONS = (
    "ClipBubble keeps the user's last 7 days of clipboard history: text, images and files copied on this machine, "
    "plus everything synced from their other machines (their Mac and others). When they mention something they "
    "copied, a screenshot they took or \"that thing from earlier\", look it up with clipboard_history (use query, kind "
    "or source to narrow it) and read it in full with clipboard_get, which returns images so you can see them. Use "
    "clipboard_set to hand the user something to paste. The mascot_* tools change the little character in their "
    "ClipBubble bubble."
)

TOOLS = [
    {"name": "clipboard_history",
     "description": "List the user's recent clipboard items (last 7 days, newest first): text, images and files copied "
                    "here or synced from their other machines. Use limit 1 for the latest copy. Returns ids for clipboard_get.",
     "inputSchema": {"type": "object", "properties": {
         "limit": {"type": "integer", "description": "Max items, default 20, max 100"},
         "kind": {"type": "string", "enum": ["text", "image", "file"]},
         "query": {"type": "string", "description": "Substring to search text, file paths and source app"},
         "source": {"type": "string", "description": "Source app name, \"Screenshot\" / \"Downloads\", or a linked "
                                                     "device's name (e.g. \"Mac\") for everything synced from it"},
         "since_hours": {"type": "number", "description": "Only items from the last N hours"}}}},
    {"name": "clipboard_get",
     "description": "Get one clipboard item in full by id: the complete text, or the image itself (and its file path), or the file paths.",
     "inputSchema": {"type": "object", "properties": {"id": {"type": "integer"}}, "required": ["id"]}},
    {"name": "clipboard_set",
     "description": "Put content on the user's clipboard so they can paste it: either new text, or an existing history item by id.",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}, "id": {"type": "integer"}}}},
    {"name": "mascot_list",
     "description": "List the mascots available for the user's ClipBubble bubble (built-in and custom) and which one is active.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "mascot_set",
     "description": "Switch the bubble's mascot by id (from mascot_list). The bubble updates live.",
     "inputSchema": {"type": "object", "properties": {"id": {"type": "string"}}, "required": ["id"]}},
    {"name": "mascot_add",
     "description": "Create a custom mascot for the bubble and (by default) switch to it. It shows at about 34px inside a "
                    "dark circle; it tilts when hovered, squishes when clicked and hops when something is copied. Three "
                    "kinds: \"blob\" = the built-in creature recolored (pass color as hex); \"emoji\" = idle/hover/active "
                    "are emoji; \"image\" = idle/hover/active are file paths (PNG, JPG, SVG) or inline SVG markup. For "
                    "images use a square canvas with a transparent background. hover and active fall back to idle.",
     "inputSchema": {"type": "object", "properties": {
         "name": {"type": "string"},
         "kind": {"type": "string", "enum": ["blob", "emoji", "image"]},
         "color": {"type": "string", "description": "Hex color for blob, like #FF6AD5"},
         "idle": {"type": "string", "description": "Emoji, image path, or inline SVG"},
         "hover": {"type": "string"},
         "active": {"type": "string", "description": "Shown when clicked or when a copy is caught"},
         "activate": {"type": "boolean", "description": "Switch to it now (default true)"}},
         "required": ["name", "kind"]}},
    {"name": "mascot_remove",
     "description": "Delete a custom mascot by id. Built-in mascots can't be removed.",
     "inputSchema": {"type": "object", "properties": {"id": {"type": "string"}}, "required": ["id"]}},
]


def text(s, error=False):
    return {"content": [{"type": "text", "text": s}], "isError": error}


def image_base64(path):
    """Downscales to 1568px max so large screenshots fit in a model's context."""
    try:
        from gi.repository import GdkPixbuf
        pb = GdkPixbuf.Pixbuf.new_from_file(path)
        w, h = pb.get_width(), pb.get_height()
        if max(w, h) > 1568:
            s = 1568 / max(w, h)
            pb = pb.scale_simple(max(1, int(w * s)), max(1, int(h * s)), GdkPixbuf.InterpType.BILINEAR)
        ok, buf = pb.save_to_bufferv("png", [], [])
        return base64.b64encode(buf).decode() if ok else None
    except Exception:
        if path.lower().endswith(".png"):
            try:
                with open(path, "rb") as f:
                    return base64.b64encode(f.read()).decode()
            except OSError:
                return None
        return None


class Server:
    def __init__(self):
        self.db = DB()
        self._shell = None

    @property
    def shell(self):
        if self._shell is None:
            from .shell import Shell
            self._shell = Shell()
        return self._shell

    def header(self, c, devices):
        frm = ""
        if c.origin:
            frm = " · from " + devices.get(c.origin, "another device") + (" (not downloaded yet)" if c.remote else "")
        return f"#{c.id} · {c.kind} · {c.source}{frm} · {time.strftime('%Y-%m-%d %H:%M', time.localtime(c.created))}"

    def call(self, name, a):
        devices = self.db.device_names()
        if name == "clipboard_history":
            limit = max(1, min(int(a.get("limit") or 20), 100))
            clips = self.db.clips(limit=limit, kind=a.get("kind"), source=a.get("source"), query=a.get("query"),
                                  since_hours=a.get("since_hours"))
            if not clips:
                return text("No clipboard items match.")
            out = []
            for c in clips:
                body = (c.file or "") if c.kind == "image" else c.text[:300] + ("…" if len(c.text) > 300 else "")
                out.append(self.header(c, devices) + "\n" + body)
            return text("\n\n".join(out))
        if name == "clipboard_get":
            c = self.db.clip(id=a.get("id")) if isinstance(a.get("id"), int) else None
            if not c:
                return text("No item with that id.", True)
            limit = 200_000
            if c.kind == "image":
                body = c.file or ""
            elif len(c.text) > limit:
                body = c.text[:limit] + f"\n… (cut: {len(c.text)} characters in all)"
            else:
                body = c.text
            content = [{"type": "text", "text": self.header(c, devices) + "\n" + body}]
            p = c.image_path
            b64 = image_base64(p) if p and not c.remote else None
            if b64:
                content.append({"type": "image", "data": b64, "mimeType": "image/png"})
            return {"content": content}
        if name == "clipboard_set":
            from . import board
            from .store import sha
            if isinstance(a.get("text"), str):
                t = a["text"]
                if not self.shell.set("text/plain;charset=utf-8", t.encode()):
                    return text("Couldn't reach the clipboard.", True)
                self.db.upsert("text", t, None, "Agent", "t:" + sha(t))
                return text("Copied text to the clipboard.")
            c = self.db.clip(id=a.get("id")) if isinstance(a.get("id"), int) else None
            if not c:
                return text("Pass text or a valid id.", True)
            if c.remote or not board.write(self.shell, c):
                return text(f"Item #{c.id}'s image or files aren't on this machine.", True)
            self.db.upsert(c.kind, c.text, c.file, c.source, c.hash)
            return text(f"Copied item #{c.id} to the clipboard.")
        if name.startswith("mascot_"):
            return self.mascot(name, a)
        return text(f"Unknown tool {name}.", True)

    def mascot(self, name, a):
        from . import mascots
        if name == "mascot_list":
            active, custom = mascots.active_id(), {m["id"] for m in mascots.custom()}
            lines = []
            for m in mascots.all_():
                line = f"{m['id']} · {m['name']} · {m['kind']}" + (" · custom" if m["id"] in custom else "")
                if m.get("color"):
                    line += " · " + m["color"]
                if m["kind"] == "emoji" and m.get("idle"):
                    line += " · " + m["idle"]
                lines.append(line + ("  ← active" if m["id"] == active else ""))
            return text("\n".join(lines))
        if name == "mascot_set":
            if not any(m["id"] == a.get("id") for m in mascots.all_()):
                return text("No mascot with that id. Use mascot_list.", True)
            mascots.set_active(a["id"])
            return text(f"Mascot set to {a['id']}.")
        if name == "mascot_add":
            try:
                m = mascots.add(a.get("name") or "Mascot", a.get("kind") or "", a.get("color"),
                                {k: a[k] for k in ("idle", "hover", "active") if isinstance(a.get(k), str)})
            except mascots.MascotError as e:
                return text(str(e), True)
            if a.get("activate", True):
                mascots.set_active(m["id"])
            return text(f"Added mascot {m['id']}.")
        if name == "mascot_remove":
            if not mascots.remove(a.get("id") or ""):
                return text("No custom mascot with that id.", True)
            return text(f"Removed mascot {a['id']}.")
        return text(f"Unknown tool {name}.", True)

    def run(self):
        for line in sys.stdin:
            try:
                msg = json.loads(line)
            except ValueError:
                if line.strip():
                    self.send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}})
                continue
            if not isinstance(msg, dict) or "id" not in msg:
                continue
            mid, params = msg["id"], msg.get("params") or {}
            method = msg.get("method")
            if method == "initialize":
                self.reply(mid, {"protocolVersion": params.get("protocolVersion", "2025-06-18"),
                                 "capabilities": {"tools": {}},
                                 "serverInfo": {"name": "clipbubble", "version": VERSION},
                                 "instructions": INSTRUCTIONS})
            elif method == "tools/list":
                self.reply(mid, {"tools": TOOLS})
            elif method == "tools/call":
                try:
                    self.reply(mid, self.call(params.get("name", ""), params.get("arguments") or {}))
                except Exception as e:
                    self.reply(mid, text(str(e), True))
            elif method == "ping":
                self.reply(mid, {})
            else:
                self.send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "Method not found"}})

    def reply(self, mid, result):
        self.send({"jsonrpc": "2.0", "id": mid, "result": result})

    def send(self, obj):
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()
