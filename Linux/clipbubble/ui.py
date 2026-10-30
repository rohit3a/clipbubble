"""The Linux app: the bubble and peek (drawn by the shell extension), and the panel and settings (GTK 4)."""
import os
import subprocess
import time
from pathlib import Path
from urllib.parse import quote

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
from gi.repository import Gdk, GdkPixbuf, Gio, GLib, Gtk, Pango  # noqa: E402

from . import board, mascots, pairing, qr  # noqa: E402
from .capture import Capture  # noqa: E402
from .engine import Engine, log, ssh_hosts, state_text  # noqa: E402
from .shell import NAME as SHELL_NAME, Shell  # noqa: E402
from .store import DB, Prefs, is_image_path, sha  # noqa: E402

TITLE = "ClipBubble"
FILTERS = [("all", "All"), ("text", "Text"), ("image", "Images"), ("file", "Files"), ("shots", "Screenshots"),
           ("downloads", "Downloads")]
TERMINALS = ("terminal", "ptyxis", "kitty", "alacritty", "foot", "wezterm", "konsole", "xterm", "tilix", "terminator",
             "ghostty", "console", "rio", "warp")
TEMP_EXTS = {"crdownload", "download", "part", "partial", "tmp", "opdownload"}
HOTKEY = "<Control><Alt>v"

CSS = """
window.cb { background: transparent; }
.panel { background: rgba(26,26,30,0.98); border-radius: 16px; border: 1px solid rgba(255,255,255,0.1); color: #f2f2f5; }
.panel entry.search, .panel entry.search:focus-within { background: none; border: none; box-shadow: none; outline: none; font-size: 16px; min-height: 48px; padding: 0 6px; }
.chip { background: none; border: none; box-shadow: none; padding: 3px 8px; border-radius: 99px; font-size: 11.5px;
        font-weight: 500; color: rgba(255,255,255,0.6); min-height: 0; }
.chip:checked { background: rgba(255,255,255,0.14); color: white; }
.sep { background: rgba(255,255,255,0.07); min-height: 1px; }
list.clips { background: none; }
list.clips row { border-radius: 9px; padding: 0 8px; min-height: 46px; margin: 0 6px; }
list.clips row:selected { background: rgba(255,255,255,0.12); }
list.clips row:hover:not(:selected) { background: rgba(255,255,255,0.06); }
.title { font-size: 13px; font-weight: 500; }
.sub { font-size: 11px; color: rgba(255,255,255,0.55); }
.dim { font-size: 11px; color: rgba(255,255,255,0.35); }
.key { font-size: 10.5px; font-weight: 600; color: rgba(255,255,255,0.55); background: rgba(255,255,255,0.09);
       border-radius: 4px; padding: 1px 5px; }
.tile { border-radius: 7px; background: rgba(255,255,255,0.06); }
.preview, .preview > viewport { background-color: #141418; }
.mono { font-family: monospace; font-size: 12px; }
.section { font-size: 10px; font-weight: 700; letter-spacing: 1px; color: rgba(255,255,255,0.35); margin-top: 14px; }
.flat { background: none; border: none; box-shadow: none; }
.get { font-size: 11px; font-weight: 600; padding: 2px 8px; min-height: 0; }
progressbar trough { min-height: 4px; } progressbar progress { min-height: 4px; }
.pet { border-radius: 99px; background: rgba(20,20,24,1); border: 1px solid rgba(255,255,255,0.1); padding: 4px; min-width: 0; min-height: 0; }
.pet.on { border: 2px solid #6E62F5; }
.emoji { font-size: 21px; }
"""


def ago(t):
    s = int(time.time() - t)
    return "now" if s < 60 else f"{s // 60}m" if s < 3600 else f"{s // 3600}h" if s < 86400 else f"{s // 86400}d"


def human(n):
    for unit in ("bytes", "KB", "MB", "GB"):
        if n < 1000 or unit == "GB":
            return f"{n:.0f} {unit}" if unit == "bytes" else f"{n:.1f} {unit}"
        n /= 1000


def is_url(s):
    t = s.strip()
    return t.startswith(("http://", "https://")) and not any(c.isspace() for c in t)


def screenshot_folder():
    try:
        pics = subprocess.run(["xdg-user-dir", "PICTURES"], capture_output=True, text=True, timeout=2).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        pics = ""
    custom = Prefs.get("screenshotFolder")
    if not pics or pics == str(Path.home()):
        pics = str(Path.home() / "Pictures")
    return Path(custom) if custom else Path(pics) / "Screenshots"


def downloads_folder():
    from .engine import downloads_dir
    return downloads_dir()


_thumbs = {}


def qr_texture(text, px=4):
    """A QR code as an image: black on white with the quiet zone, `px` pixels a module."""
    m = qr.encode(text)
    n = len(m) + 8
    def row(y):
        return bytes(c for x in range(n) for c in (0 if 4 <= y < n - 4 and 4 <= x < n - 4 and m[y - 4][x - 4] else 255,) * 3 * px)
    data = b"".join(row(y) * px for y in range(n))
    pixbuf = GdkPixbuf.Pixbuf.new_from_bytes(GLib.Bytes.new(data), GdkPixbuf.Colorspace.RGB, False, 8, n * px, n * px, n * px * 3)
    return Gdk.Texture.new_for_pixbuf(pixbuf)


def thumb(path, px):
    key = (path, px)
    if key not in _thumbs:
        try:
            pb = GdkPixbuf.Pixbuf.new_from_file_at_scale(path, px, px, True)
            _thumbs[key] = Gdk.Texture.new_for_pixbuf(pb)
        except GLib.Error:
            _thumbs[key] = None
        if len(_thumbs) > 300:
            _thumbs.pop(next(iter(_thumbs)))
    return _thumbs[key]


class Watcher:
    """New files in one folder, handed to on_new(path, mtime)."""

    def __init__(self, folder, include, on_new, max_age=None):
        self.folder, self.include, self.on_new, self.max_age = Path(folder), include, on_new, max_age
        self.folder.mkdir(parents=True, exist_ok=True)
        self.monitor, self.poll = None, 0
        try:
            self.monitor = Gio.File.new_for_path(str(self.folder)).monitor_directory(Gio.FileMonitorFlags.WATCH_MOVES, None)
            self.monitor.connect("changed", self.changed)
        except GLib.Error:
            # Out of inotify instances (a busy desktop hits the limit of 128): look every few seconds instead.
            log(f"polling {self.folder}")
            self.seen = time.time()
            self.poll = GLib.timeout_add_seconds(3, self.scan)

    def scan(self):
        newest = self.seen
        try:
            for e in os.scandir(self.folder):
                t = e.stat().st_mtime
                # A second's grace so a file still being written is picked up once it's done.
                if self.seen < t <= time.time() - 1 and self.include(e.path):
                    self.on_new(e.path, t)
                    newest = max(newest, t)
        except OSError:
            pass
        self.seen = newest
        return True

    def changed(self, _m, f, other, event):
        if event in (Gio.FileMonitorEvent.MOVED_IN, Gio.FileMonitorEvent.RENAMED):
            f = other if event == Gio.FileMonitorEvent.RENAMED else f
        elif event != Gio.FileMonitorEvent.CHANGES_DONE_HINT:
            return
        p = f.get_path() if f else None
        if not p or not self.include(p):
            return
        try:
            st = os.stat(p)
        except OSError:
            return
        if self.max_age and time.time() - st.st_mtime > self.max_age:
            return
        self.on_new(p, st.st_mtime)

    def existing(self, days=7):
        cutoff = time.time() - days * 86400
        out = []
        try:
            for e in os.scandir(self.folder):
                if self.include(e.path) and e.stat().st_mtime >= cutoff:
                    out.append((e.path, e.stat().st_mtime))
        except OSError:
            pass
        return out

    def stop(self):
        if self.monitor:
            self.monitor.cancel()
        if self.poll:
            GLib.source_remove(self.poll)


class App(Gtk.Application):
    def __init__(self):
        super().__init__(application_id="studio.krnl.ClipBubble.App", flags=Gio.ApplicationFlags.DEFAULT_FLAGS)
        self.db = DB()
        self.shell = Shell()
        self.engine = Engine()
        self.clips, self.selection, self.query, self.filter = [], None, "", "all"
        self.unseen = 0
        self.transfers = {}
        self.previewing = False
        self.shown_at = 0
        self.menu = None
        self.watchers = {}
        self.last_image_capture = 0
        self.last_screenshot = 0

    # Startup

    def do_activate(self):
        if getattr(self, "win", None):
            return
        self.hold()
        provider = Gtk.CssProvider()
        provider.load_from_string(CSS)
        Gtk.StyleContext.add_provider_for_display(Gdk.Display.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        Gtk.Settings.get_default().set_property("gtk-application-prefer-dark-theme", True)
        self.build_panel()

        self.capture = Capture(self.shell, on_change=self.captured_from_board)
        self.engine.apply = self.apply_synced
        self.engine.on_received = lambda: GLib.idle_add(self.captured)
        self.engine.on_status = lambda: GLib.idle_add(self.statuses_changed)
        self.engine.on_transfers = lambda t: GLib.idle_add(self.transfers_changed, t)
        self.engine.start()
        self.shell.subscribe(HotKey=lambda _a: self.toggle(), BubbleClicked=self.toggle,
                             BubbleMoved=lambda c: Prefs.set("corner", c), PeekPicked=self.peek_picked)
        # The extension comes and goes with the shell (log out and in): give it the bubble each time it appears.
        Gio.bus_watch_name(Gio.BusType.SESSION, SHELL_NAME, Gio.BusNameWatcherFlags.NONE,
                           lambda *_: self.shell_appeared(), None)
        self.sync_watchers()
        self.import_folders()
        # Agents change the mascot from their own process (clipbubble --mcp).
        self.mascot_monitors = []
        try:
            for path in (mascots.SETTINGS, mascots.FOLDER):
                m = Gio.File.new_for_path(str(path)).monitor(Gio.FileMonitorFlags.NONE, None)
                m.connect("changed", lambda *_: GLib.timeout_add(150, self.mascot_changed))
                self.mascot_monitors.append(m)
        except GLib.Error:
            self.mascot_stamp = self.mascot_files()
            GLib.timeout_add_seconds(2, self.poll_mascot)
        GLib.timeout_add_seconds(3600, self.hourly)

    def shell_appeared(self):
        self.shell.set_hotkey(HOTKEY)
        self.push_bubble()
        self.push_peek()

    def hourly(self):
        self.db.prune()
        self.import_folders()
        return True

    # Bubble and peek

    def push_bubble(self):
        try:
            frames = mascots.frames()
        except Exception as e:
            log(f"mascot: {e!r}")
            frames = {}
        self.shell.set_bubble({"visible": True, "frames": frames, "badge": self.unseen,
                               "paused": bool(Prefs.get("sync.paused")) and bool(Prefs.get("sync.hosts")),
                               "corner": Prefs.get("corner", "br"), "panelOpen": self.win.get_visible()})

    def mascot_files(self):
        out = []
        for p in (mascots.SETTINGS, mascots.FOLDER):
            try:
                out.append(os.stat(p).st_mtime)
            except OSError:
                out.append(0)
        return out

    def poll_mascot(self):
        now = self.mascot_files()
        if now != self.mascot_stamp:
            self.mascot_stamp = now
            self.mascot_changed()
        return True

    def mascot_changed(self):
        self.push_bubble()
        self.shell.pulse()
        if self.stack.get_visible_child_name() == "settings":
            self.fill_pets()
        return False

    def peek_row(self, c):
        icon = c.image_path if c.image_path and not c.remote and os.path.exists(c.image_path) else None
        return {"title": self.title_of(c), "subtitle": self.subtitle_of(c), "icon": icon}

    def push_peek(self):
        self.recent = self.db.clips(limit=3)
        self.shell.set_peek([self.peek_row(c) for c in self.recent], "Ctrl+Alt+V for all")

    def peek_picked(self, i, copy_only):
        if 0 <= i < len(getattr(self, "recent", [])):
            self.pick(self.recent[i], not copy_only)

    def captured_from_board(self):
        """A copy made on this machine. A screenshot's own clipboard copy is already in history as its file."""
        newest = self.db.clips(limit=1)
        if newest and newest[0].kind == "image":
            self.last_image_capture = time.time()
            if time.time() - self.last_screenshot < 5:
                self.db.delete(hash=newest[0].hash)
        self.captured()

    def captured(self):
        self.push_peek()
        if self.win.get_visible():
            self.reload()
        else:
            self.unseen += 1
        self.push_bubble()
        self.shell.pulse()
        return False

    # Synced items

    def apply_synced(self, c):
        """Engine thread: a copy from another machine goes on this clipboard."""
        if not board.write(self.shell, c):
            log(f"couldn't put {c.hash[:10]} on the clipboard")

    def statuses_changed(self):
        if self.stack.get_visible_child_name() == "settings":
            self.refresh_hosts()
        return False

    def transfers_changed(self, t):
        finished = any(h not in t for h in self.transfers)
        self.transfers = t
        if self.win.get_visible():
            if finished:
                self.reload()
            else:
                for row in self.rows:
                    row.update_transfer(t.get(row.clip.hash))
        return False

    # Folders

    def sync_watchers(self):
        def want(key, make):
            if Prefs.on(key) and key not in self.watchers:
                try:
                    self.watchers[key] = make()
                except (OSError, GLib.Error) as e:
                    log(f"can't watch {key}: {e!r}")
            elif not Prefs.on(key) and key in self.watchers:
                self.watchers.pop(key).stop()

        def shot(p):
            return is_image_path(p) or p.lower().endswith((".mp4", ".webm", ".mkv"))

        def on_shot(p, t):
            self.last_screenshot = time.time()
            self.db.insert_if_missing("file", p, "Screenshot", "f:" + sha(p), t)
            # GNOME already put the screenshot on the clipboard; keep only the file in history.
            newest = self.db.clips(limit=2)
            for c in newest:
                if c.kind == "image" and time.time() - self.last_image_capture < 5 and board.same_picture(c.file, p):
                    self.db.delete(hash=c.hash)
            self.captured()

        def download(p):
            name = os.path.basename(p)
            return name != "ClipBubble" and not name.startswith(".") and name.rsplit(".", 1)[-1].lower() not in TEMP_EXTS

        def on_download(p, t):
            self.db.insert_if_missing("file", p, "Downloads", "f:" + sha(p), t)
            self.captured()

        want("screenshots", lambda: Watcher(screenshot_folder(), shot, on_shot, max_age=120))
        want("downloads", lambda: Watcher(downloads_folder(), download, on_download))

    def import_folders(self):
        for source, key in (("Screenshot", "screenshots"), ("Downloads", "downloads")):
            w = self.watchers.get(key)
            for p, t in (w.existing() if w else []):
                self.db.insert_if_missing("file", p, source, "f:" + sha(p), t)
        self.push_peek()

    # Picking

    def pick(self, c, paste):
        if c.remote:
            self.engine.get(c.hash)
            return
        ok = board.write(self.shell, c)
        self.hide_panel()
        if not ok:
            Gdk.Display.get_default().beep()
            return
        # Ours on the clipboard means capture skips it, so move it to the top here.
        self.db.upsert(c.kind, c.text, c.file, c.source, c.hash)
        self.push_peek()
        if paste and Prefs.get("autoPaste", True):
            GLib.timeout_add(160, self.paste)

    def paste(self):
        w = self.shell.focused()
        name = (w.get("app", "") + " " + w.get("wmClass", "")).lower()
        self.shell.paste(any(t in name for t in TERMINALS))
        return False

    # Panel

    def build_panel(self):
        self.win = Gtk.Window(application=self, title=TITLE, decorated=False, resizable=False)
        self.win.add_css_class("cb")
        self.win.set_default_size(420, 470)
        self.win.set_hide_on_close(True)
        self.win.connect("notify::is-active", self.focus_changed)
        keys = Gtk.EventControllerKey()
        keys.set_propagation_phase(Gtk.PropagationPhase.CAPTURE)
        keys.connect("key-pressed", self.key)
        self.win.add_controller(keys)

        self.stack = Gtk.Stack(transition_type=Gtk.StackTransitionType.CROSSFADE, transition_duration=80)
        self.stack.add_css_class("panel")
        self.stack.add_named(self.build_main(), "main")
        self.stack.add_named(self.build_settings(), "settings")
        self.win.set_child(self.stack)

    def build_main(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        top = Gtk.Box(spacing=6, margin_start=14, margin_end=10)
        top.append(Gtk.Image.new_from_icon_name("system-search-symbolic"))
        self.search = Gtk.Entry(placeholder_text="Search clipboard…", hexpand=True)
        self.search.add_css_class("search")
        self.search.connect("changed", lambda e: self.set_query(e.get_text()))
        top.append(self.search)
        box.append(top)

        chips = Gtk.Box(spacing=2, margin_start=10, margin_end=12, margin_bottom=8)
        self.chips = {}
        group = None
        for tag, title in FILTERS:
            b = Gtk.ToggleButton(label=title)
            if group:
                b.set_group(group)
            group = group or b
            b.add_css_class("chip")
            b.connect("toggled", lambda b, tag=tag: b.get_active() and self.set_filter(tag))
            self.chips[tag] = b
            chips.append(b)
        spacer = Gtk.Box(hexpand=True)
        chips.append(spacer)
        tab = Gtk.Label(label="tab")
        tab.add_css_class("dim")
        chips.append(tab)
        box.append(chips)
        box.append(self.sep())

        self.listbox = Gtk.ListBox(selection_mode=Gtk.SelectionMode.SINGLE, activate_on_single_click=True)
        self.listbox.add_css_class("clips")
        self.listbox.connect("row-activated", lambda _l, row: self.pick(row.clip, True))
        self.listbox.connect("row-selected", lambda _l, row: setattr(self, "selection", row.clip.id if row else None))
        self.rows = []
        scroll = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER, child=self.listbox)
        scroll.set_margin_top(6)
        self.scroll = scroll
        self.empty = Gtk.Label(label="Copy anything — it lands here", vexpand=True)
        self.empty.add_css_class("sub")
        self.preview = Gtk.ScrolledWindow(visible=False)
        self.preview.add_css_class("preview")
        overlay = Gtk.Overlay(child=scroll, vexpand=True)
        overlay.add_overlay(self.empty)
        overlay.add_overlay(self.preview)
        box.append(overlay)
        box.append(self.sep())

        foot = Gtk.Box(spacing=14, margin_start=10, margin_end=14, margin_top=6, margin_bottom=6)
        gear = Gtk.Button(icon_name="emblem-system-symbolic", tooltip_text="Settings")
        gear.add_css_class("flat")
        gear.connect("clicked", lambda _b: self.show_settings())
        foot.append(gear)
        foot.append(Gtk.Box(hexpand=True))
        for k, label in (("↩", "Paste"), ("Alt↩", "Copy"), ("Ctrl Y", "Preview"), ("Del", "Delete")):
            pair = Gtk.Box(spacing=5)
            l = Gtk.Label(label=label)
            l.add_css_class("sub")
            kk = Gtk.Label(label=k)
            kk.add_css_class("key")
            pair.append(l)
            pair.append(kk)
            foot.append(pair)
        box.append(foot)
        return box

    def sep(self):
        s = Gtk.Box()
        s.add_css_class("sep")
        return s

    def set_query(self, q):
        if q != self.query:
            self.query = q
            self.reload()

    def set_filter(self, tag):
        if tag != self.filter:
            self.filter = tag
            self.reload()

    def reload(self):
        kind = self.filter if self.filter in ("text", "image", "file") else None
        source = {"shots": "Screenshot", "downloads": "Downloads"}.get(self.filter)
        self.clips = self.db.clips(kind=kind, source=source, query=self.query or None)
        self.devices = self.db.device_names() if any(c.origin for c in self.clips) else {}
        self.listbox.remove_all()
        self.rows = []
        for i, c in enumerate(self.clips[:300]):
            row = ClipRow(self, c, i)
            self.rows.append(row)
            self.listbox.append(row)
        self.empty.set_visible(not self.clips)
        self.empty.set_label("Copy anything — it lands here" if not self.query else "No matches")
        keep = next((r for r in self.rows if r.clip.id == self.selection), self.rows[0] if self.rows else None)
        if keep:
            self.listbox.select_row(keep)
        if self.previewing:
            self.show_preview()

    def selected(self):
        row = self.listbox.get_selected_row()
        return row.clip if row else None

    def move(self, d):
        if not self.rows:
            return
        row = self.listbox.get_selected_row()
        i = self.rows.index(row) if row in self.rows else 0
        target = self.rows[min(max(i + d, 0), len(self.rows) - 1)]
        self.listbox.select_row(target)
        target.grab_focus()
        self.search.grab_focus_without_selecting()
        if self.previewing:
            self.show_preview()

    def cycle_filter(self, d):
        tags = [t for t, _ in FILTERS]
        self.chips[tags[(tags.index(self.filter) + d) % len(tags)]].set_active(True)

    def delete(self, c):
        i = next((n for n, r in enumerate(self.rows) if r.clip.id == c.id), 0)
        self.db.delete(id=c.id)
        self.selection = self.clips[i + 1].id if i + 1 < len(self.clips) else (self.clips[i - 1].id if i > 0 else None)
        self.reload()
        self.push_peek()

    def show_preview(self):
        c = self.selected()
        if not c:
            return
        if c.image_path and not c.remote and os.path.exists(c.image_path):
            pic = Gtk.Picture.new_for_filename(c.image_path)
            pic.set_content_fit(Gtk.ContentFit.CONTAIN)
            pic.set_margin_top(12)
            pic.set_margin_bottom(12)
            child = pic
        else:
            shown = c.text[:100_000] if c.kind == "text" else "\n".join(c.paths) or c.text
            if c.kind == "text" and len(shown) < len(c.text):
                shown += "\n\n… the rest is too long to preview, but it pastes in full"
            child = Gtk.Label(label=shown, selectable=True, wrap=True, xalign=0, yalign=0, margin_start=14,
                              margin_end=14, margin_top=14, margin_bottom=14)
            child.add_css_class("mono" if c.kind == "text" else "title")
        self.preview.set_child(child)
        self.preview.set_visible(True)

    def key(self, _c, keyval, _code, state):
        ctrl = bool(state & Gdk.ModifierType.CONTROL_MASK)
        alt = bool(state & Gdk.ModifierType.ALT_MASK)
        name = Gdk.keyval_name(keyval) or ""
        if self.stack.get_visible_child_name() == "settings":
            if name == "Escape":
                self.stack.set_visible_child_name("main")
                self.search.grab_focus()
                return True
            return False
        if name == "Down":
            self.move(1)
        elif name == "Up":
            self.move(-1)
        elif name in ("Return", "KP_Enter"):
            c = self.selected()
            if c:
                self.pick(c, not alt)
        elif name in ("Tab", "ISO_Left_Tab"):
            self.cycle_filter(-1 if state & Gdk.ModifierType.SHIFT_MASK else 1)
        elif name == "Escape":
            if self.previewing:
                self.previewing = False
                self.preview.set_visible(False)
            elif self.query:
                self.search.set_text("")
            else:
                self.hide_panel()
        elif (name == "Delete" and not self.query) or (name == "BackSpace" and ctrl):
            c = self.selected()
            if c:
                self.delete(c)
        elif ctrl and name.lower() == "y":
            self.previewing = not self.previewing
            self.show_preview() if self.previewing else self.preview.set_visible(False)
        elif ctrl and name.isdigit() and name != "0" and int(name) <= len(self.clips):
            self.pick(self.clips[int(name) - 1], not alt)
        else:
            return False
        return True

    def toggle(self):
        self.hide_panel() if self.win.get_visible() else self.show_panel()

    def show_panel(self):
        self.search.set_text("")
        self.query = ""
        self.chips["all"].set_active(True)
        self.filter = "all"
        self.previewing = False
        self.preview.set_visible(False)
        self.stack.set_visible_child_name("main")
        self.selection = None
        self.reload()
        self.unseen = 0
        self.win.set_opacity(0)
        self.win.present()
        self.shown_at = time.monotonic()
        GLib.timeout_add(30, self.place_panel)

    def place_panel(self, tries=0):
        if not self.win.get_visible():
            return False
        r = self.shell.bubble_rect() or (0, 0, 0, 0)
        a = self.shell.work_area() or (0, 0, 1920, 1080)
        w, h = 420, 470
        bx, by, bw, bh = r
        right, bottom = bx + bw / 2 > a[0] + a[2] / 2, by + bh / 2 > a[1] + a[3] / 2
        x = bx + bw - w - 9 if right else bx + 9
        y = by - h + 4 if bottom else by + bh - 4
        x = min(max(x, a[0] + 8), a[0] + a[2] - w - 8)
        y = min(max(y, a[1] + 8), a[1] + a[3] - h - 8)
        # The window may not be mapped yet, and GNOME places it once when it is: keep it see-through until it's
        # been put by the bubble twice.
        if not self.shell.place(TITLE, int(x), int(y), True) or tries == 0:
            if tries < 30:
                GLib.timeout_add(40, self.place_panel, tries + 1)
                return False
        self.shell.activate(TITLE)
        self.win.set_opacity(1)
        self.search.grab_focus()
        self.push_bubble()
        return False

    def hide_panel(self):
        if self.win.get_visible():
            self.win.set_visible(False)
            self.push_bubble()

    def focus_changed(self, *_):
        # Clicking anywhere else closes it, like the Mac panel. A moment's grace while it's being placed, and none
        # while a row's menu is open, since that takes the focus.
        if (not self.win.is_active() and self.win.get_visible() and not self.menu
                and time.monotonic() - self.shown_at > 0.4):
            self.hide_panel()
        return False

    # Row text

    def title_of(self, c):
        if c.kind == "image":
            return "Image"
        if c.kind == "file":
            return ", ".join(os.path.basename(p) for p in c.paths) or c.text
        line = next((l.strip() for l in c.text[:20000].splitlines() if l.strip()), "")[:200]
        return line.replace("https://", "").replace("http://", "") if is_url(line) else line

    def subtitle_of(self, c):
        t = self.transfers.get(c.hash)
        if t:
            return f"{'Sending to' if t.sending else 'Getting from'} {t.peer} · {human(t.done)} of {human(t.total)}"
        parts = [c.source, ago(c.created)]
        if c.origin:
            parts.insert(0, getattr(self, "devices", {}).get(c.origin) or self.db.device_names().get(c.origin, "Other device"))
        if c.remote:
            parts.append("not downloaded yet")
        elif c.kind == "file" and c.paths:
            parts.append(os.path.dirname(c.paths[0]).replace(str(Path.home()), "~", 1))
        elif c.kind == "text":
            n = c.text[:20000].count("\n") + 1
            if n > 1:
                parts.append(f"{n} lines")
        return " · ".join(p for p in parts if p)

    # Settings

    def show_settings(self):
        self.refresh_settings()
        self.stack.set_visible_child_name("settings")

    def build_settings(self):
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        head = Gtk.Box(spacing=8, margin_start=10, margin_end=16, margin_top=8, margin_bottom=8)
        back = Gtk.Button(icon_name="go-previous-symbolic")
        back.add_css_class("flat")
        back.connect("clicked", lambda _b: (self.stack.set_visible_child_name("main"), self.search.grab_focus()))
        head.append(back)
        t = Gtk.Label(label="Settings", hexpand=True, xalign=0)
        t.add_css_class("title")
        head.append(t)
        esc = Gtk.Label(label="esc")
        esc.add_css_class("dim")
        head.append(esc)
        outer.append(head)
        outer.append(self.sep())
        self.settings_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, margin_start=16, margin_end=16,
                                    margin_bottom=16)
        outer.append(Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER, child=self.settings_box))
        return outer

    def section(self, title):
        l = Gtk.Label(label=title.upper(), xalign=0)
        l.add_css_class("section")
        self.settings_box.append(l)

    def note(self, text):
        l = Gtk.Label(label=text, xalign=0, wrap=True)
        l.add_css_class("dim")
        self.settings_box.append(l)
        return l

    def switch_row(self, title, detail, active, on_set):
        row = Gtk.Box(spacing=10, margin_top=5, margin_bottom=5)
        text = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, hexpand=True)
        t = Gtk.Label(label=title, xalign=0)
        t.add_css_class("title")
        text.append(t)
        sub = None
        if detail is not None:
            sub = Gtk.Label(label=detail, xalign=0, wrap=True)
            sub.add_css_class("sub")
            text.append(sub)
        row.append(text)
        sw = Gtk.Switch(active=active, valign=Gtk.Align.CENTER)
        sw.connect("notify::active", lambda s, _p: on_set(s.get_active()))
        row.append(sw)
        self.settings_box.append(row)
        return sub

    def set_pref(self, key, on):
        Prefs.set("save." + key, on)
        self.sync_watchers()
        self.import_folders()
        self.push_peek()

    def refresh_settings(self):
        box = self.settings_box
        while box.get_first_child():
            box.remove(box.get_first_child())
        self.section("Save to history")
        for key, title, _sql in Prefs.CATEGORIES:
            if key in ("screenshots", "downloads", "iphone"):
                continue
            self.switch_row(title, None, Prefs.on(key), lambda on, k=key: self.set_pref(k, on))
        self.note("Turning one off stops saving it and hides what's already there, for your agents too.")
        self.section("Folders")
        self.switch_row("Screenshots", str(screenshot_folder()).replace(str(Path.home()), "~", 1), Prefs.on("screenshots"),
                        lambda on: self.set_pref("screenshots", on))
        self.switch_row("Recent downloads", str(downloads_folder()).replace(str(Path.home()), "~", 1), Prefs.on("downloads"),
                        lambda on: self.set_pref("downloads", on))
        self.switch_row("Auto-paste", "↩ pastes into the window you were in", Prefs.get("autoPaste", True),
                        lambda on: Prefs.set("autoPaste", on))
        if not self.shell.version():
            self.note("Log out and back in once so GNOME loads ClipBubble's helper: it watches copies, shows the "
                      "bubble and pastes. Copies from your other machines already land on this clipboard.")

        self.section("Sync over SSH")
        self.note("Copy on one machine, paste on another, using your SSH config and keys.")
        b = Gtk.Button(label="Link iPhone…", halign=Gtk.Align.START, margin_top=4)
        b.connect("clicked", self.start_pairing)
        self.settings_box.append(b)
        self.pair_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, margin_top=4)
        self.settings_box.append(self.pair_box)
        self.show_pairing()
        self.host_labels = {}
        hosts = ssh_hosts()
        enabled = set(Prefs.get("sync.hosts", []))
        if not hosts:
            self.note("No hosts in ~/.ssh/config yet. Machines that link to this one show up by themselves.")
        for h in hosts:
            self.host_labels[h] = self.switch_row(h, "", h in enabled, lambda on, h=h: self.set_host(h, on))
        self.switch_row("Pause sync", None, bool(Prefs.get("sync.paused")), self.set_paused)
        self.linked_label = self.note("")
        self.refresh_hosts()

        self.section("Mascot")
        self.pets = Gtk.FlowBox(min_children_per_line=7, max_children_per_line=7, homogeneous=True,
                                selection_mode=Gtk.SelectionMode.NONE, column_spacing=6, row_spacing=6, margin_top=4)
        self.settings_box.append(self.pets)
        self.fill_pets()

        self.section("AI agents (MCP)")
        self.note("Let Claude Code, Codex, Cursor and other agents on this machine search your history, including "
                  "copies from your other machines.")
        b = Gtk.Button(label="Copy setup prompt", halign=Gtk.Align.START, margin_top=4)
        b.connect("clicked", self.copy_prompt)
        self.settings_box.append(b)

        self.section("General")
        login = subprocess.run(["systemctl", "--user", "is-enabled", "clipbubble.service"], capture_output=True,
                               text=True).stdout.strip() == "enabled"
        self.switch_row("Open at login", None, login, lambda on: subprocess.run(
            ["systemctl", "--user", "enable" if on else "disable", "clipbubble.service"], capture_output=True))
        actions = Gtk.Box(spacing=10, margin_top=6)
        clear = Gtk.Button(label="Clear all history…")
        clear.connect("clicked", self.clear_all)
        actions.append(clear)
        actions.append(Gtk.Box(hexpand=True))
        quit_ = Gtk.Button(label="Quit ClipBubble")
        quit_.connect("clicked", lambda _b: self.quit_app())
        actions.append(quit_)
        self.settings_box.append(actions)

    def start_pairing(self, _b):
        if getattr(self, "pairing", None):
            self.pairing.stop()
        self.pairing = None
        if not pairing.ssh_server_on():
            self.pair_state = (None, "Start the SSH server first, so the iPhone can reach this computer. "
                                     "On Ubuntu or Debian: sudo apt install openssh-server")
        else:
            self.pairing = pairing.Server(self.engine.name, lambda name: GLib.idle_add(self.pairing_done, name),
                                          lambda: GLib.idle_add(self.pairing_done, None))
            hint = pairing.firewall_hint()
            self.pair_state = (self.pairing.link(), "Scan with the iPhone's Camera, or in ClipBubble on the iPhone. "
                                                    "It needs the same Wi-Fi as this computer." + (f" {hint}." if hint else ""))
        self.show_pairing()

    def pairing_done(self, name):
        self.pairing = None
        self.pair_state = (None, f"Linked {name}. It syncs whenever ClipBubble is open there." if name else
                           "That code expired. Press Link iPhone… for a new one.")
        self.show_pairing()
        return False

    def show_pairing(self):
        box = getattr(self, "pair_box", None)
        if box is None:
            return
        while box.get_first_child():
            box.remove(box.get_first_child())
        link, text = getattr(self, "pair_state", (None, None))
        if link:
            box.append(Gtk.Picture(paintable=qr_texture(link), can_shrink=False, halign=Gtk.Align.START))
        if text:
            l = Gtk.Label(label=text, xalign=0, wrap=True)
            l.add_css_class("dim")
            box.append(l)

    def refresh_hosts(self):
        paused = bool(Prefs.get("sync.paused"))
        for h, label in getattr(self, "host_labels", {}).items():
            st = self.engine.statuses.get(h)
            label.set_label("Paused" if paused and h in Prefs.get("sync.hosts", []) else state_text(st) if st else "")
        if getattr(self, "linked_label", None):
            names = [p.name for p in self.engine.linked]
            self.linked_label.set_label(f"This machine shows up as {self.engine.name}." +
                                        (f" Linked now: {', '.join(names)}." if names else "") +
                                        " Files up to 25 MB move on their own.")
        return False

    def set_host(self, h, on):
        hosts = [x for x in Prefs.get("sync.hosts", []) if x != h] + ([h] if on else [])
        Prefs.set("sync.hosts", hosts)
        self.engine.post(self.engine.reconcile_links)

    def set_paused(self, on):
        Prefs.set("sync.paused", on)

        def go():
            if on:
                for s in list(self.engine.sessions):
                    s.close()
            self.engine.reconcile_links()
        self.engine.post(go)
        self.push_bubble()

    def fill_pets(self):
        self.pets.remove_all()
        current = mascots.active_id()
        for m in mascots.all_():
            try:
                f = mascots.frames(m).get("idle")
            except Exception:
                f = None
            if f and not f.startswith("text:"):
                pic = Gtk.Picture.new_for_filename(f)
            else:
                pic = Gtk.Label(label=f[5:] if f else m["name"][:2])
                pic.add_css_class("emoji")
            pic.set_size_request(30, 30)
            b = Gtk.Button(child=pic, tooltip_text=m["name"], halign=Gtk.Align.CENTER)
            b.add_css_class("pet")
            if m["id"] == current:
                b.add_css_class("on")
            b.connect("clicked", lambda _b, mid=m["id"]: (mascots.set_active(mid), self.fill_pets(), self.push_bubble()))
            self.pets.append(b)

    def copy_prompt(self, button):
        binary = str(Path.home() / ".local/bin/clipbubble")
        prompt = (f"Connect yourself to ClipBubble, my clipboard history app, via its MCP server.\n\n"
                  f"It's a local stdio MCP server: command \"{binary}\", argument \"--mcp\". No API key or network.\n\n"
                  f"Register it with whatever agent you are:\n"
                  f"- Claude Code: claude mcp add --scope user clipbubble -- \"{binary}\" --mcp\n"
                  f"- Codex: codex mcp add clipbubble -- \"{binary}\" --mcp\n"
                  f"- Cursor, Claude Desktop, Windsurf or anything with a JSON MCP config: add\n"
                  f"  \"clipbubble\": {{ \"command\": \"{binary}\", \"args\": [\"--mcp\"] }} under \"mcpServers\"\n\n"
                  f"Then check it works by calling clipboard_history with limit 3 (restart or reload MCP servers first if "
                  f"you need to), and tell me what you found. Tools: clipboard_history, clipboard_get, clipboard_set, "
                  f"mascot_list, mascot_set, mascot_add, mascot_remove.")
        # Marked as ours, so the prompt itself doesn't land in history.
        self.shell.set("text/plain;charset=utf-8", prompt.encode())
        button.set_label("Copied! Paste it into your agent")
        GLib.timeout_add(1600, lambda: button.set_label("Copy setup prompt") or False)

    def clear_all(self, _b):
        d = Gtk.AlertDialog(message="Clear all history?", detail="This deletes every item here, and on your linked machines.",
                            buttons=["Cancel", "Clear"], cancel_button=0, default_button=0)

        def done(dlg, res):
            try:
                if dlg.choose_finish(res) == 1:
                    self.db.delete_all()
                    self.reload()
                    self.push_peek()
            except GLib.Error:
                pass
        d.choose(self.win, None, done)

    def quit_app(self):
        self.shell.set_bubble({"visible": False})
        subprocess.run(["systemctl", "--user", "stop", "--no-block", "clipbubble.service"], capture_output=True)
        self.quit()


class ClipRow(Gtk.ListBoxRow):
    def __init__(self, app, clip, index):
        super().__init__()
        self.app, self.clip = app, clip
        box = Gtk.Box(spacing=10)
        box.append(self.tile())
        text = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, hexpand=True, valign=Gtk.Align.CENTER)
        title = Gtk.Label(label=app.title_of(clip) or " ", xalign=0, ellipsize=Pango.EllipsizeMode.END, max_width_chars=40)
        title.add_css_class("title")
        self.sub = Gtk.Label(label=app.subtitle_of(clip), xalign=0, ellipsize=Pango.EllipsizeMode.END, max_width_chars=48)
        self.sub.add_css_class("sub")
        text.append(title)
        text.append(self.sub)
        box.append(text)
        self.side = Gtk.Box(spacing=6, valign=Gtk.Align.CENTER)
        box.append(self.side)
        if index < 9:
            hint = Gtk.Label(label=f"Ctrl {index + 1}")
            hint.add_css_class("dim")
            box.append(hint)
        self.set_child(box)
        self.update_transfer(app.transfers.get(clip.hash))

        menu = Gtk.GestureClick(button=Gdk.BUTTON_SECONDARY)
        menu.connect("pressed", lambda *_: self.open_menu())
        self.add_controller(menu)
        drag = Gtk.DragSource(actions=Gdk.DragAction.COPY)
        drag.connect("prepare", self.drag_content)
        self.add_controller(drag)

    def tile(self):
        c = self.clip
        size = 34
        if c.image_path and not c.remote and os.path.exists(c.image_path):
            tex = thumb(c.image_path, 72)
            if tex:
                pic = Gtk.Picture.new_for_paintable(tex)
                pic.set_content_fit(Gtk.ContentFit.COVER)
                pic.set_size_request(size, size)
                pic.add_css_class("tile")
                pic.set_overflow(Gtk.Overflow.HIDDEN)
                return pic
        if c.kind == "file":
            ctype, _ = Gio.content_type_guess(c.paths[0] if c.paths else c.text, None)
            img = Gtk.Image.new_from_gicon(Gio.content_type_get_icon(ctype))
        elif is_url(c.text):
            img = Gtk.Image.new_from_icon_name("web-browser-symbolic")
        else:
            img = Gtk.Image.new_from_icon_name("text-x-generic-symbolic")
        img.set_pixel_size(22)
        frame = Gtk.Box(hexpand=False, valign=Gtk.Align.CENTER, width_request=size, height_request=size)
        img.set_hexpand(True)
        frame.append(img)
        frame.add_css_class("tile")
        return frame

    def update_transfer(self, t):
        while self.side.get_first_child():
            self.side.remove(self.side.get_first_child())
        self.sub.set_label(self.app.subtitle_of(self.clip))
        if t:
            bar = Gtk.ProgressBar(fraction=t.fraction, valign=Gtk.Align.CENTER, width_request=44)
            self.side.append(bar)
            stop = Gtk.Button(icon_name="window-close-symbolic", tooltip_text="Stop")
            stop.add_css_class("flat")
            stop.connect("clicked", lambda _b: self.app.engine.cancel_transfer(self.clip.hash))
            self.side.append(stop)
        elif self.clip.remote:
            get = Gtk.Button(label="Get", tooltip_text="Download it from the other machine")
            get.add_css_class("get")
            get.connect("clicked", lambda _b: self.app.engine.get(self.clip.hash))
            self.side.append(get)

    def drag_content(self, _src, _x, _y):
        c = self.clip
        paths = [c.image_path] if c.kind == "image" and c.image_path else c.paths
        if paths and not c.remote:
            files = [Gio.File.new_for_path(p) for p in paths if os.path.exists(p)]
            return Gdk.ContentProvider.new_for_value(Gdk.FileList.new_from_list(files)) if files else None
        return Gdk.ContentProvider.new_for_value(c.text)

    def open_menu(self):
        c, app = self.clip, self.app
        pop = Gtk.Popover(has_arrow=False)
        pop.set_parent(self)
        app.menu = pop

        def closed(p):
            app.menu = None
            GLib.idle_add(p.unparent)
            # A click elsewhere closed the menu: the panel goes too.
            GLib.timeout_add(120, app.focus_changed)
        pop.connect("closed", closed)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)

        def item(label, fn):
            b = Gtk.Button(label=label)
            b.add_css_class("flat")
            b.get_child().set_xalign(0)
            b.connect("clicked", lambda _b: (pop.popdown(), fn()))
            box.append(b)

        if not c.remote:
            item("Paste", lambda: app.pick(c, True))
            item("Copy", lambda: app.pick(c, False))
        p = c.image_path or (c.paths[0] if c.paths else None)
        if p and not c.remote and os.path.exists(p):
            item("Open", lambda: Gio.AppInfo.launch_default_for_uri("file://" + quote(p), None))
            item("Show in Files", lambda: subprocess.Popen(
                ["dbus-send", "--session", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                 "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                 f"array:string:file://{quote(p)}", "string:"]))
        t = app.transfers.get(c.hash)
        if c.remote and not t:
            item(f"Get from {app.devices.get(c.origin, 'other machine')}", lambda: app.engine.get(c.hash))
        elif c.kind != "text" and not c.remote:
            for peer in app.engine.linked:
                item(f"Send to {peer.name}", lambda d=peer.device: app.engine.send_to(c.hash, d))
        if t:
            item("Stop transfer", lambda: app.engine.cancel_transfer(c.hash))
        item("Delete", lambda: app.delete(c))
        pop.set_child(box)
        pop.popup()


def run():
    App().run([])
