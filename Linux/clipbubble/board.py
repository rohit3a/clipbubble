"""Reads and writes the clipboard in the same three kinds the Mac uses: text, images and files."""
import os
from urllib.parse import quote, unquote, urlparse

from .store import IMAGES, is_image_path, sha
from .sync import MAX_TEXT

TEXT_TYPES = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING"]
SECRET_TYPES = {"x-kde-passwordManagerHint"}


def png_data(path):
    if path.lower().endswith(".png"):
        try:
            with open(path, "rb") as f:
                return f.read()
        except OSError:
            return None
    try:
        from gi.repository import GdkPixbuf
        ok, buf = GdkPixbuf.Pixbuf.new_from_file(path).save_to_bufferv("png", [], [])
        return buf if ok else None
    except Exception:
        return None


def to_png(data, mime):
    if mime == "image/png":
        return data
    try:
        from gi.repository import GdkPixbuf
        loader = GdkPixbuf.PixbufLoader()
        loader.write(data)
        loader.close()
        ok, buf = loader.get_pixbuf().save_to_bufferv("png", [], [])
        return buf if ok else None
    except Exception:
        return None


def write(shell, c):
    """Puts a clip on the clipboard. False, leaving the clipboard alone, when its image or files are gone."""
    if c.kind == "text":
        return shell.set("text/plain;charset=utf-8", c.text.encode("utf-8"))
    if c.kind == "image":
        d = png_data(c.file) if c.file else None
        return bool(d) and shell.set("image/png", d)
    paths = [p for p in c.paths if os.path.isabs(p) and os.path.exists(p)]
    if not paths:
        return False
    # One image file goes on as its pixels, so it pastes into chat and web apps and through Live screen.
    if len(paths) == 1 and is_image_path(paths[0]):
        d = png_data(paths[0])
        if d:
            return shell.set("image/png", d)
    uris = "\n".join("file://" + quote(p) for p in paths)
    return shell.set("x-special/gnome-copied-files", ("copy\n" + uris).encode())


def _file_paths(shell, mimes):
    for mime in ("x-special/gnome-copied-files", "text/uri-list"):
        if mime not in mimes:
            continue
        raw = shell.read(mime)
        if not raw:
            continue
        lines = [l.strip() for l in raw.decode("utf-8", "replace").splitlines()]
        uris = [l for l in lines if l.startswith("file://")]
        if uris:
            return [unquote(urlparse(u).path) for u in uris]
    return None


def read(shell, mimes):
    """(kind, text, file, hash, png) for what's on the clipboard now, or None. An image isn't written anywhere yet."""
    paths = _file_paths(shell, mimes)
    if paths:
        joined = "\n".join(paths)
        return ("file", joined, None, "f:" + sha(joined), None)
    raw = None
    for mime in TEXT_TYPES:
        if mime in mimes:
            raw = shell.read(mime)
            if raw is not None:
                break
    text = raw.decode("utf-8", "replace") if raw else ""
    stripped = text.strip()
    looks_like_url = stripped.startswith("http") and " " not in stripped and "\n" not in stripped
    if not stripped or looks_like_url:
        image = next((m for m in ["image/png"] + sorted(mimes) if m.startswith("image/") and m in mimes), None)
        data = shell.read(image) if image else None
        png = to_png(data, image) if data else None
        if png:
            h = sha(png)
            return ("image", "", str(IMAGES / (h + ".png")), "i:" + h, png)
    if stripped and len(raw) <= MAX_TEXT:
        return ("text", text, None, "t:" + sha(text), None)
    return None
