"""Saves every copy into history, with the Mac's rules: settle first, skip secrets, drop paste-throughs."""
import time

from . import board
from .store import DB, Prefs


def app_name(window):
    app = window.get("app") or window.get("wmClass") or ""
    if app:
        try:
            from gi.repository import Gio
            info = Gio.DesktopAppInfo.new(app + ".desktop")
            if info:
                return info.get_name()
        except Exception:
            pass
    return window.get("wmClass") or window.get("title") or "Linux"


class Capture:
    SETTLE_MS = 600

    def __init__(self, shell, on_change=lambda: None):
        from gi.repository import GLib
        self.GLib = GLib
        self.shell = shell
        self.db = DB()
        self.on_change = on_change
        self.timer = 0
        self.mimes = []
        self.ours = False
        self.last = None  # (hash, at, inserted)
        self.previous = None
        shell.subscribe(self.changed)

    def changed(self, mimes, ours):
        self.mimes, self.ours = mimes, ours
        if self.timer:
            self.GLib.source_remove(self.timer)
        self.timer = self.GLib.timeout_add(self.SETTLE_MS, self.settled)

    def settled(self):
        self.timer = 0
        mimes = set(self.mimes)
        # What sync or the panel just put there is already in history.
        if self.ours or not mimes or mimes & board.SECRET_TYPES:
            return False
        clip = board.read(self.shell, mimes)
        if not clip:
            return False
        kind, text, file, h = clip
        if self.last and h == self.last[0] and self.db.exists(h):
            return False
        source = app_name(self.shell.focused())
        if not Prefs.on({"image": "images", "file": "files"}.get(kind, "text")):
            return False
        now = time.time()
        # The previous contents came straight back, so what we just saw was a paste-through: drop it.
        if h == self.previous and self.last and now - self.last[1] < 2.5:
            if self.last[2]:
                self.db.delete(hash=self.last[0])
            self.last = (h, now, False)
            self.previous = None
            self.on_change()
            return False
        inserted = not self.db.exists(h)
        self.db.upsert(kind, text, file, source, h)
        self.previous = self.last[0] if self.last else None
        self.last = (h, now, inserted)
        self.on_change()
        return False
