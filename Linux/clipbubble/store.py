"""History in SQLite, with the same schema and rules as the Mac app, so sync means the same thing everywhere."""
import hashlib
import json
import os
import sqlite3
import threading
import time
from dataclasses import dataclass
from pathlib import Path

from .sync import Stamp

RETENTION = 7 * 24 * 3600
IMAGE_EXTS = {"png", "jpg", "jpeg", "heic", "tiff", "tif", "gif", "webp", "bmp"}


def _data_dir():
    if os.environ.get("CLIPBUBBLE_HOME"):
        return Path(os.environ["CLIPBUBBLE_HOME"])
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local/share") / "clipbubble"


DIR = _data_dir()
IMAGES = DIR / "images"
DB_PATH = DIR / "clips.sqlite"


def socket_path():
    run = os.environ.get("XDG_RUNTIME_DIR")
    if run and not os.environ.get("CLIPBUBBLE_HOME"):
        return os.path.join(run, "clipbubble.sock")
    return str(DIR / "peer.sock")


def sha(data):
    if isinstance(data, str):
        data = data.encode("utf-8")
    return hashlib.sha256(data).hexdigest()


def is_image_path(p):
    return p.rsplit(".", 1)[-1].lower() in IMAGE_EXTS if "." in p else False


@dataclass
class Clip:
    id: int
    kind: str
    text: str
    file: str
    source: str
    created: float
    hash: str
    origin: str
    remote: bool

    @property
    def paths(self):
        return [p for p in self.text.split("\n") if p] if self.kind == "file" else []

    @property
    def image_path(self):
        if self.kind == "image":
            return self.file
        return next((p for p in self.paths if is_image_path(p)), None)


class Prefs:
    """Settings in a small JSON file. The same keys as the Mac's defaults."""
    CATEGORIES = [
        ("text", "Copied text", "kind = 'text' AND source <> 'iPhone'"),
        ("images", "Copied images", "kind = 'image' AND source <> 'iPhone'"),
        ("files", "Copied files", "kind = 'file' AND source NOT IN ('Screenshot', 'Downloads', 'iPhone')"),
        ("screenshots", "Screenshots", "source = 'Screenshot'"),
        ("downloads", "Recent downloads", "source = 'Downloads'"),
        ("iphone", "Copies from iPhone", "source = 'iPhone'"),
    ]
    _lock = threading.Lock()

    @classmethod
    def path(cls):
        return DIR / "prefs.json"

    @classmethod
    def load(cls):
        try:
            return json.loads(cls.path().read_text())
        except (OSError, ValueError):
            return {}

    @classmethod
    def get(cls, key, default=None):
        return cls.load().get(key, default)

    @classmethod
    def set(cls, key, value):
        with cls._lock:
            d = cls.load()
            d[key] = value
            DIR.mkdir(parents=True, exist_ok=True)
            tmp = cls.path().with_suffix(".tmp")
            tmp.write_text(json.dumps(d, indent=1))
            os.replace(tmp, cls.path())

    @classmethod
    def on(cls, key):
        return cls.get("save." + key, True)

    @classmethod
    def hidden_sql(cls):
        d = cls.load()
        return "".join(f" AND NOT ({sql})" for key, _, sql in cls.CATEGORIES if not d.get("save." + key, True))


# Called with the hashes of every insert, bump or delete, from any DB in this process. The engine sets it.
changed = None


def _notify(hashes):
    if changed and hashes:
        changed(list(hashes))


class DB:
    def __init__(self):
        IMAGES.mkdir(parents=True, exist_ok=True)
        os.chmod(DIR, 0o700)
        self.c = sqlite3.connect(DB_PATH, timeout=3, isolation_level=None, check_same_thread=False)
        self.c.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS clips(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL DEFAULT '',
              file TEXT, source TEXT NOT NULL DEFAULT '', hash TEXT UNIQUE NOT NULL, created REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS clips_created ON clips(created);
            CREATE TABLE IF NOT EXISTS forgotten(hash TEXT PRIMARY KEY, at REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, os TEXT NOT NULL DEFAULT '');
        """)
        if self.c.execute("PRAGMA user_version").fetchone()[0] < 1:
            self.c.executescript("""
                BEGIN;
                ALTER TABLE clips ADD COLUMN origin TEXT NOT NULL DEFAULT '';
                ALTER TABLE clips ADD COLUMN remote INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 1;
                COMMIT;
            """)

    def _select(self, tail, args=()):
        rows = self.c.execute("SELECT id,kind,text,file,source,created,hash,origin,remote FROM clips " + tail, args)
        return [Clip(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], bool(r[8])) for r in rows]

    def upsert(self, kind, text, file, source, hash):
        """Inserts a clip, or moves an identical existing one to the top."""
        self.c.execute("""
            INSERT INTO clips(kind,text,file,source,hash,created) VALUES(?,?,?,?,?,?)
            ON CONFLICT(hash) DO UPDATE SET created=excluded.created,
              source=CASE WHEN clips.source IN ('Screenshot', 'Downloads') THEN clips.source ELSE excluded.source END
            """, (kind, text, file, source, hash, time.time()))
        _notify([hash])

    def insert_if_missing(self, kind, text, source, hash, created):
        """Adds a clip with its own time, unless it was deleted after that time."""
        cur = self.c.execute("""
            INSERT OR IGNORE INTO clips(kind,text,source,hash,created)
            SELECT ?,?,?,?,? WHERE NOT EXISTS (SELECT 1 FROM forgotten WHERE hash = ? AND at >= ?)
            """, (kind, text, source, hash, created, hash, created))
        if cur.rowcount > 0:
            _notify([hash])

    def clips(self, limit=300, kind=None, source=None, query=None, since_hours=None):
        tail = "WHERE created >= ?"
        args = [time.time() - (since_hours * 3600 if since_hours else RETENTION)]
        if kind == "image":
            tail += " AND (kind = 'image' OR (kind = 'file' AND (" + " OR ".join(f"text LIKE '%.{e}'" for e in IMAGE_EXTS) + ")))"
        elif kind:
            tail += " AND kind = ?"
            args.append(kind)
        if source:
            tail += " AND (source = ? COLLATE NOCASE OR origin IN (SELECT id FROM devices WHERE name = ? COLLATE NOCASE))"
            args += [source, source]
        if query:
            q = "%" + query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
            tail += " AND (text LIKE ? ESCAPE '\\' OR source LIKE ? ESCAPE '\\')"
            args += [q, q]
        tail += Prefs.hidden_sql() + " ORDER BY created DESC LIMIT ?"
        args.append(limit)
        return self._select(tail, args)

    def clip(self, id=None, hash=None):
        if hash is not None:
            r = self._select("WHERE hash = ?", (hash,))
        else:
            r = self._select("WHERE id = ?" + Prefs.hidden_sql(), (id,))
        return r[0] if r else None

    def exists(self, hash):
        return self.clip(hash=hash) is not None

    def _remove_files(self, clips):
        for c in clips:
            if c.file and os.path.dirname(c.file) == str(IMAGES):
                try:
                    os.remove(c.file)
                except OSError:
                    pass

    def _forget(self, clips, at=None):
        at = time.time() if at is None else at
        for c in clips:
            self.c.execute("INSERT OR REPLACE INTO forgotten(hash, at) VALUES(?, ?)", (c.hash, at))

    def delete(self, id=None, hash=None):
        rows = self._select("WHERE hash = ?", (hash,)) if hash is not None else self._select("WHERE id = ?", (id,))
        self._forget(rows)
        self._remove_files(rows)
        for c in rows:
            self.c.execute("DELETE FROM clips WHERE id = ?", (c.id,))
        _notify([c.hash for c in rows])

    def delete_all(self):
        rows = self._select("")
        self._forget(rows)
        self._remove_files(rows)
        self.c.execute("DELETE FROM clips")
        _notify([c.hash for c in rows])

    def prune(self):
        cutoff = time.time() - RETENTION
        self._remove_files(self._select("WHERE created < ?", (cutoff,)))
        self.c.execute("DELETE FROM clips WHERE created < ?", (cutoff,))
        self.c.execute("DELETE FROM forgotten WHERE at < ?", (cutoff - 86400,))

    # Sync

    def stamps(self, including_waiting=True):
        """including_waiting=False leaves out synced items whose bytes haven't arrived, so peers offer them again."""
        cutoff = time.time() - RETENTION
        out = {}
        waiting = "" if including_waiting else " AND remote = 0"
        rows = self.c.execute(f"""
            SELECT hash, created, 0 FROM clips WHERE created >= ?{waiting}{Prefs.hidden_sql()}
            UNION ALL SELECT hash, at, 1 FROM forgotten WHERE at >= ?""", (cutoff, cutoff))
        for h, t, d in rows:
            s = Stamp(h, t, d == 1)
            if s.beats(out.get(h)):
                out[h] = s
        return list(out.values())

    def stamp(self, hash):
        best = None
        for t, d in self.c.execute("SELECT created, 0 FROM clips WHERE hash = ? UNION ALL SELECT at, 1 FROM forgotten WHERE hash = ?",
                                   (hash, hash)):
            s = Stamp(hash, t, d == 1)
            if s.beats(best):
                best = s
        return best

    def allowed(self, kind, source):
        return self.c.execute("SELECT 1 FROM (SELECT ? AS kind, ? AS source) WHERE 1" + Prefs.hidden_sql(),
                              (kind, source)).fetchone() is not None

    def insert(self, e, origin):
        names = e.text if e.kind == "file" else ""
        self.c.execute("""
            INSERT INTO clips(kind,text,source,hash,created,origin,remote) VALUES(?,?,?,?,?,?,?)
            ON CONFLICT(hash) DO UPDATE SET created = max(created, excluded.created)
            """, (e.kind, e.text if e.kind == "text" else names, e.source, e.hash, e.time, origin, 1 if e.blobs else 0))
        _notify([e.hash])

    def bump(self, hash, to):
        self.c.execute("UPDATE clips SET created = ? WHERE hash = ? AND created < ?", (to, hash, to))
        _notify([hash])

    def tombstone(self, st):
        rows = self._select("WHERE hash = ? AND created <= ?", (st.hash, st.time))
        self._remove_files(rows)
        self.c.execute("DELETE FROM clips WHERE hash = ? AND created <= ?", (st.hash, st.time))
        self.c.execute("INSERT INTO forgotten(hash, at) VALUES(?, ?) ON CONFLICT(hash) DO UPDATE SET at = max(at, excluded.at)",
                       (st.hash, st.time))
        _notify([st.hash])

    def received(self, hash, file=None, text=None):
        if text is not None:
            self.c.execute("UPDATE clips SET remote = 0, text = ? WHERE hash = ?", (text, hash))
        else:
            self.c.execute("UPDATE clips SET remote = 0, file = ? WHERE hash = ?", (file, hash))

    def save_device(self, id, name, os_name):
        """Whether this was news: a device not seen before, or one with a new name."""
        return self.c.execute("INSERT INTO devices(id,name,os) VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name, "
                              "os=excluded.os WHERE name != excluded.name OR os != excluded.os", (id, name, os_name)).rowcount > 0

    def devices(self):
        return self.c.execute("SELECT id, name, os FROM devices").fetchall()

    def device_names(self):
        return dict(self.c.execute("SELECT id, name FROM devices"))
