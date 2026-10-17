"""Bubble mascots, stored like the Mac's, and drawn into small PNGs per mood for the shell extension to show."""
import json
import math
import os
import re
import shutil
from pathlib import Path

from .store import DIR, sha

FOLDER = DIR / "mascots"
SETTINGS = DIR / "settings.json"
FRAMES = DIR / "cache" / "mascot"
SCALE = 2   # drawn at twice the 34 px it shows at, for HiDPI

BUILTINS = [
    {"id": "blob", "name": "Blob", "kind": "blob", "color": "#8E7CFF"},
    {"id": "mint", "name": "Mint", "kind": "blob", "color": "#34D399"},
    {"id": "peach", "name": "Peach", "kind": "blob", "color": "#FF9466"},
    {"id": "sky", "name": "Sky", "kind": "blob", "color": "#4FC3F7"},
    {"id": "cat", "name": "Cat", "kind": "emoji", "idle": "🐱", "hover": "😺", "active": "😸"},
    {"id": "frog", "name": "Frog", "kind": "emoji", "idle": "🐸"},
    {"id": "ghost", "name": "Ghost", "kind": "emoji", "idle": "👻"},
    {"id": "robot", "name": "Robot", "kind": "emoji", "idle": "🤖"},
    {"id": "classic", "name": "Classic icon", "kind": "icon"},
]


class MascotError(Exception):
    pass


def hex_rgb(s):
    t = (s or "").strip().lstrip("#")
    if not re.fullmatch(r"[0-9a-fA-F]{6}", t):
        return None
    v = int(t, 16)
    return ((v >> 16) & 255) / 255, ((v >> 8) & 255) / 255, (v & 255) / 255


def custom():
    out = []
    for p in sorted(FOLDER.glob("*.json")) if FOLDER.exists() else []:
        try:
            out.append(json.loads(p.read_text()))
        except (OSError, ValueError):
            pass
    return sorted(out, key=lambda m: m.get("name", "").lower())


def all_():
    return BUILTINS + custom()


def active_id():
    try:
        return json.loads(SETTINGS.read_text()).get("mascot", "blob")
    except (OSError, ValueError):
        return "blob"


def active():
    a = active_id()
    return next((m for m in all_() if m["id"] == a), BUILTINS[0])


def set_active(mid):
    DIR.mkdir(parents=True, exist_ok=True)
    SETTINGS.write_text(json.dumps({"mascot": mid}))


def path_of(f):
    return f if f.startswith("/") else str(FOLDER / f)


def add(name, kind, color=None, states=None):
    """Saves a custom mascot. For images each state is a file path or inline SVG; files are copied in."""
    states = states or {}
    if kind not in ("blob", "emoji", "image"):
        raise MascotError("kind must be blob, emoji or image")
    if kind == "blob" and not hex_rgb(color):
        raise MascotError("blob needs color as a hex like #FF6AD5")
    if kind != "blob" and not (states.get("idle") or "").strip():
        raise MascotError(f"{kind} needs an idle value")
    FOLDER.mkdir(parents=True, exist_ok=True)
    base = re.sub(r"-+", "-", "".join(c if c.isalnum() else "-" for c in name.lower())).strip("-") or "mascot"
    taken = {m["id"] for m in all_()}
    mid, n = base, 2
    while mid in taken:
        mid, n = f"{base}-{n}", n + 1
    m = {"id": mid, "name": name, "kind": kind}
    if kind == "blob":
        m["color"] = color
    copied = []
    try:
        for state in ("idle", "hover", "active"):
            value = (states.get(state) or "").strip()
            if not value:
                continue
            if kind == "image":
                if value.startswith("<svg") or value.startswith("<?xml"):
                    f = f"{mid}-{state}.svg"
                    (FOLDER / f).write_text(value)
                else:
                    src = Path(os.path.expanduser(value))
                    f = f"{mid}-{state}{src.suffix.lower()}"
                    try:
                        shutil.copyfile(src, FOLDER / f)
                    except OSError:
                        raise MascotError(f"couldn't read the {state} image at {src}")
                copied.append(FOLDER / f)
                value = f
            m[state] = value
        (FOLDER / f"{mid}.json").write_text(json.dumps(m, indent=1, sort_keys=True))
    except Exception:
        for p in copied:
            p.unlink(missing_ok=True)
        raise
    return m


def remove(mid):
    m = next((m for m in custom() if m["id"] == mid), None)
    if not m:
        return False
    files = {mid + ".json"}
    if m.get("kind") == "image":
        files |= {m[k] for k in ("idle", "hover", "active") if m.get(k) and not m[k].startswith("/")}
    for f in files:
        (FOLDER / os.path.basename(f)).unlink(missing_ok=True)
    if active_id() == mid:
        set_active("blob")
    return True


# Drawing. Coordinates follow the Mac's 34 pt face, centered at (17, 17).

def _ellipse(cr, cx, cy, w, h):
    cr.save()
    cr.translate(cx, cy)
    cr.scale(w / 2, h / 2)
    cr.arc(0, 0, 1, 0, 2 * math.pi)
    cr.restore()


def _arc(cr, cx, cy, w, h, up, closed=False):
    x0, x1 = cx - w / 2, cx + w / 2
    y0 = cy + h / 2 if up else cy - h / 2
    ctrl = cy - h / 2 - h if up else cy + h / 2 + h
    cr.move_to(x0, y0)
    # The quadratic curve from the Mac, as a cubic.
    cr.curve_to(x0 + 2 / 3 * (cx - x0), y0 + 2 / 3 * (ctrl - y0), x1 + 2 / 3 * (cx - x1), y0 + 2 / 3 * (ctrl - y0), x1, y0)
    if closed:
        cr.close_path()


def _blob(cr, rgb, mood):
    import cairo
    _ellipse(cr, 17, 17, 33, 30)
    cr.set_source_rgb(*rgb)
    cr.fill_preserve()
    g = cairo.LinearGradient(0, 2, 0, 17)
    g.add_color_stop_rgba(0, 1, 1, 1, 0.45)
    g.add_color_stop_rgba(1, 1, 1, 1, 0)
    cr.set_source(g)
    cr.fill_preserve()
    g = cairo.LinearGradient(0, 17, 0, 32)
    g.add_color_stop_rgba(0, 0, 0, 0, 0)
    g.add_color_stop_rgba(1, 0, 0, 0, 0.18)
    cr.set_source(g)
    cr.fill()
    cr.set_line_cap(cairo.LINE_CAP_ROUND)
    for ex in (17 - 6.75, 17 + 6.75):
        if mood == "active":
            _arc(cr, ex, 14.5, 6, 3.5, up=True)
            cr.set_source_rgba(0, 0, 0, 0.8)
            cr.set_line_width(1.8)
            cr.stroke()
            continue
        sy = 0.12 if mood == "blink" else 1
        h = 9.5 if mood == "hover" else 8.5
        cr.save()
        cr.translate(ex, 14.5)
        cr.scale(1, sy)
        cr.new_sub_path()
        r = 3.25
        cr.arc(0, -h / 2 + r, r, math.pi, 0)
        cr.arc(0, h / 2 - r, r, 0, math.pi)
        cr.close_path()
        cr.set_source_rgb(1, 1, 1)
        cr.fill()
        py = -1 if mood == "hover" else 1
        cr.arc(0, py, 2, 0, 2 * math.pi)
        cr.set_source_rgba(0, 0, 0, 0.85)
        cr.fill()
        cr.arc(0.8, -1.8 if mood == "hover" else 0.2, 0.7, 0, 2 * math.pi)
        cr.set_source_rgb(1, 1, 1)
        cr.fill()
        cr.restore()
    for cx in (17 - 10.25, 17 + 10.25):
        _ellipse(cr, cx, 20.5, 4.5, 3.5)
        cr.set_source_rgba(1, 0.45, 0.6, 0.55)
        cr.fill()
    if mood == "active":
        _arc(cr, 17, 23.5, 8, 4.5, up=False, closed=True)
        cr.set_source_rgba(0, 0, 0, 0.75)
        cr.fill()
    elif mood == "hover":
        _ellipse(cr, 17, 23.5, 3.5, 4)
        cr.set_source_rgba(0, 0, 0, 0.7)
        cr.fill()
    else:
        _arc(cr, 17, 23.5, 6, 2.5, up=False)
        cr.set_source_rgba(0, 0, 0, 0.7)
        cr.set_line_width(1.5)
        cr.stroke()


def frames(m=None):
    """{mood: frame} for the extension: a png path, or "text:" and an emoji, which the shell draws itself (Python
    can't hand cairo to Pango without python3-gi-cairo). Pngs are named by content, so a changed mascot never shows a
    stale one."""
    m = m or active()
    kind = m.get("kind")
    if kind == "image":
        idle = m.get("idle")
        out = {k: path_of(m[k]) for k in ("idle", "hover", "active") if m.get(k)}
        return out if idle else {}
    if kind == "emoji":
        return {mood: "text:" + (m.get(mood) or (m.get("hover") if mood == "active" else None) or m.get("idle") or "🙂")
                for mood in ("idle", "hover", "active")}
    if kind != "blob":
        return {"idle": "text:📋", "hover": "text:📋", "active": "text:✅"}
    import cairo
    FRAMES.mkdir(parents=True, exist_ok=True)
    out = {}
    for mood in ("idle", "hover", "active", "blink"):
        key = f"blob-{m.get('color')}-{mood}-v1"
        path = FRAMES / f"{sha(key)[:16]}.png"
        if not path.exists():
            surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, 34 * SCALE, 34 * SCALE)
            cr = cairo.Context(surface)
            cr.scale(SCALE, SCALE)
            _blob(cr, hex_rgb(m.get("color")) or (0.56, 0.49, 1), mood)
            tmp = path.with_suffix(".tmp")
            surface.write_to_png(str(tmp))
            os.replace(tmp, path)
        out[mood] = str(path)
    return out
