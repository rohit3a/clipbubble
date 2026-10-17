"""Talks to the ClipBubble GNOME Shell extension over D-Bus, with wl-copy for writing until the extension loads."""
import json
import shutil
import subprocess

NAME = "studio.krnl.ClipBubble.Shell"
PATH = "/studio/krnl/ClipBubble/Shell"
IFACE = NAME


class Shell:
    def __init__(self):
        from gi.repository import Gio
        self.Gio = Gio
        try:
            self.bus = Gio.bus_get_sync(Gio.BusType.SESSION)
        except Exception:
            self.bus = None

    def call(self, method, sig=None, args=(), timeout=3000):
        from gi.repository import GLib
        if self.bus is None:
            return None
        try:
            return self.bus.call_sync(NAME, PATH, IFACE, method, GLib.Variant(sig, args) if sig else None,
                                      None, self.Gio.DBusCallFlags.NONE, timeout, None)
        except Exception:
            return None

    def version(self):
        r = self.call("Version", timeout=1000)
        return r.unpack()[0] if r else None

    def mimetypes(self):
        r = self.call("Mimetypes")
        return r.unpack()[0] if r else []

    def read(self, mime):
        r = self.call("ReadClipboard", "(s)", (mime,), timeout=5000)
        return r.get_child_value(0).get_data_as_bytes().get_data() if r else None

    def set(self, mime, data):
        """Puts data on the clipboard. The extension tags it as ours so capture skips it."""
        if self.call("SetClipboard", "(say)", (mime, data)) is not None:
            return True
        if not shutil.which("wl-copy"):
            return False
        try:
            # wl-copy stays behind to serve the data; detached stdio keeps it from holding an ssh session open.
            p = subprocess.Popen(["wl-copy", "--type", mime], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL, start_new_session=True)
            p.stdin.write(data)
            p.stdin.close()
            p.wait(timeout=5)
            return p.returncode == 0
        except (OSError, subprocess.SubprocessError):
            return False

    def paste(self, shift):
        return self.call("Paste", "(b)", (shift,)) is not None

    def focused(self):
        r = self.call("FocusedWindow")
        try:
            return json.loads(r.unpack()[0]) if r else {}
        except ValueError:
            return {}

    def set_hotkey(self, accel):
        r = self.call("SetHotKey", "(s)", (accel,))
        return bool(r and r.unpack()[0])

    def place(self, title, x, y, above=True):
        r = self.call("Place", "(siib)", (title, x, y, above))
        return bool(r and r.unpack()[0])

    def pointer(self):
        r = self.call("Pointer")
        return r.unpack() if r else None

    def work_area(self):
        r = self.call("WorkArea")
        return r.unpack() if r else None

    def activate(self, title):
        r = self.call("Activate", "(s)", (title,))
        return bool(r and r.unpack()[0])

    def set_bubble(self, state):
        self.call("SetBubble", "(s)", (json.dumps(state),))

    def bubble_rect(self):
        r = self.call("BubbleRect")
        return r.unpack() if r else None

    def pulse(self):
        self.call("Pulse")

    def set_peek(self, rows, hint):
        self.call("SetPeek", "(s)", (json.dumps({"rows": rows, "hint": hint}),))

    def subscribe(self, on_clipboard=None, **handlers):
        """Signals arrive on the thread running the default GLib main loop, whenever the extension is loaded.
        handlers maps signal names (HotKey, BubbleClicked, BubbleMoved, PeekPicked) to callables taking its args."""
        if self.bus is None:
            return
        if on_clipboard:
            handlers["ClipboardChanged"] = on_clipboard

        def got(_c, _sender, _path, _iface, signal, params):
            fn = handlers.get(signal)
            if fn:
                fn(*(params.unpack() if params else ()))

        self.bus.signal_subscribe(None, IFACE, None, PATH, None, self.Gio.DBusSignalFlags.NONE, got)
