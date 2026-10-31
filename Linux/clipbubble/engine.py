"""Links this machine's history with ClipBubble elsewhere over SSH (docs/sync-protocol.md).

Engine state is touched only on the engine thread, which runs posted callables in order, like the Mac's serial
queue. Each session has a reader thread and a writer thread; writes put control frames ahead of file chunks."""
import collections
import dataclasses
import os
import queue
import shutil
import socket
import subprocess
import threading
import time
import uuid
from pathlib import Path

from . import store, sync
from .store import DB, DIR, IMAGES, sha
from .sync import CHUNK, Entry, Hello, Stamp, Blob

VERSION = "1.6.2"


def log(line):
    path = DIR / "sync.log"
    try:
        if path.exists() and path.stat().st_size > 1_000_000:
            os.replace(path, path.with_suffix(".log.1"))
        with open(path, "a") as f:
            f.write(time.strftime("%Y-%m-%dT%H:%M:%SZ ", time.gmtime()) + line + "\n")
    except OSError:
        pass


def downloads_dir():
    if os.environ.get("CLIPBUBBLE_DOWNLOADS"):
        return Path(os.environ["CLIPBUBBLE_DOWNLOADS"])
    try:
        out = subprocess.run(["xdg-user-dir", "DOWNLOAD"], capture_output=True, text=True, timeout=2).stdout.strip()
        if out and out != str(Path.home()):
            return Path(out)
    except (OSError, subprocess.SubprocessError):
        pass
    return Path.home() / "Downloads"


def received_folder(device):
    return downloads_dir() / "ClipBubble" / safe_file_name(device)


def safe_file_name(raw):
    """A name from another machine can't climb out of its folder or hide itself."""
    s = raw.replace("/", "_").replace("\\", "_").replace(":", "_")
    s = "".join(ch for ch in s if ch not in "\r\n\0")
    s = s.lstrip(".")[:200].strip()
    return s or "file"


def unique_path(folder, name):
    p = folder / name
    base, dot, ext = name.rpartition(".")
    if not dot:
        base, ext = name, ""
    n = 2
    while p.exists():
        p = folder / (f"{base} ({n}).{ext}" if ext else f"{base} ({n})")
        n += 1
    return p


class Engine:
    def __init__(self, name=None):
        self.q = queue.Queue()
        DIR.mkdir(parents=True, exist_ok=True)
        id_file = DIR / "device-id"
        try:
            self.device = id_file.read_text().strip()
        except OSError:
            self.device = ""
        if not self.device:
            self.device = str(uuid.uuid4())
            id_file.write_text(self.device)
        self.name = name or os.environ.get("CLIPBUBBLE_DEVICE_NAME") or socket.gethostname()
        self.sessions = []
        self.links = {}
        self.sent_at = {}
        self.hooked = False
        self.db = None
        # Puts a synced item on the clipboard. Swapped by tests so they never touch the real one.
        self.apply = lambda clip: None
        # History changed, or a device's name did.
        self.on_received = lambda: None
        # After on_received: that change was an item arriving or moving up, which the bubble announces.
        self.on_new = lambda: None
        self.on_status = lambda: None
        # Called on the engine thread with {hash: Transfer} about four times a second while files move.
        self.on_transfers = lambda transfers: None
        self.statuses = {}
        self.linked = []
        self.wanted = set()
        self.ticking = False
        self.last_transfers = {}
        self.thread = threading.Thread(target=self._run, name="clipbubble-sync", daemon=True)
        self.thread.start()

    def _run(self):
        self.db = DB()
        while True:
            fn = self.q.get()
            try:
                fn()
            except Exception as e:  # one bad frame or file must not stop sync
                log(f"error: {e!r}")

    def post(self, fn):
        self.q.put(fn)

    def drop_phones(self):
        """A phone just lost its key. Which session was its isn't known, so all of them drop and the others link again."""
        self.post(lambda: [s.close() for s in list(self.sessions) if s.peer and s.peer.os == "ios"])

    # Hosts

    def start(self):
        self.post(self._serve_socket)
        self.post(self.reconcile_links)
        self.post(self._watch_other_writers)

    def _watch_other_writers(self):
        """Agents (clipbubble --mcp) write history from their own process. SQLite's data_version moves when another
        connection commits, so a once-a-second check costs nothing while nothing changes."""
        version, known = None, {}

        def check():
            nonlocal version, known
            v = self.db.c.execute("PRAGMA data_version").fetchone()[0]
            if v != version:
                now = {st.hash: st for st in self.db.stamps()}
                if version is not None and self.sessions:
                    self.broadcast([h for h, st in now.items() if known.get(h) != st])
                version, known = v, now

        def tick():
            while True:
                self.post(check)
                time.sleep(1)

        threading.Thread(target=tick, daemon=True, name="clipbubble-watch").start()

    def reconcile_links(self):
        want = set() if store.Prefs.get("sync.paused", False) else set(store.Prefs.get("sync.hosts", []))
        for alias in list(self.links):
            if alias not in want:
                self.links.pop(alias).stop()
        for alias in want:
            if alias not in self.links:
                link = HostLink(alias, self)
                self.links[alias] = link
                link.connect()
        self.publish_status()

    def publish_status(self):
        self.statuses = {a: l.state for a, l in self.links.items()}
        self.linked = [s.peer for s in self.sessions if s.peer]
        self.on_status()

    # Manual transfers

    def get(self, h):
        """Get: downloads an item that's still on another device, whatever its size."""
        def go():
            self.wanted.add(h)
            s = next((s for s in self.sessions if h in s.offered), None)
            if s:
                s.download(self.make_download(s.offered[h], s, False))
                self.watch_transfers()
        self.post(go)

    def send_to(self, h, device):
        """Send to: asks a linked device to fetch one of this machine's items now."""
        def go():
            s = next((s for s in self.sessions if s.summarized and s.peer and s.peer.device == device), None)
            if not s:
                return
            st = self.db.stamp(h)
            if st and st.beats(s.known.get(h)):
                self.send(st, s)
            s.send((sync.OFFER, h))
        self.post(go)

    def cancel_transfer(self, h):
        """Stops a transfer either way and throws away what arrived."""
        def go():
            self.wanted.discard(h)
            for s in self.sessions:
                if s.is_transferring(h):
                    s.cancel_download(h)
                    s.cancel_upload(h)
                    s.send((sync.CANCEL, h))
            self.publish_transfers()
        self.post(go)

    def make_download(self, e, s, apply):
        return Download(e.hash, e.kind, [b.name for b in e.blobs], [b.sha for b in e.blobs], [b.size for b in e.blobs],
                        s.peer.name if s.peer else "Device", apply)

    def watch_transfers(self):
        """Progress ticks only while something is moving, so an idle link costs nothing."""
        if self.ticking:
            return self.publish_transfers()
        self.ticking = True

        def tick():
            while self.ticking:
                self.post(self.publish_transfers)
                time.sleep(0.25)

        threading.Thread(target=tick, daemon=True, name="clipbubble-progress").start()

    def publish_transfers(self):
        all_ = {}
        for s in self.sessions:
            for h, t in s.transfers().items():
                all_.setdefault(h, t)
        if not all_:
            self.ticking = False
        if all_ != self.last_transfers:
            self.last_transfers = all_
            self.on_transfers(all_)

    def _serve_socket(self):
        path = store.socket_path()
        try:
            os.unlink(path)
        except OSError:
            pass
        srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old = os.umask(0o177)
        try:
            srv.bind(path)
        finally:
            os.umask(old)
        srv.listen(8)

        def accept():
            while True:
                c, _ = srv.accept()
                f = c.detach()
                self.post(lambda f=f: self.begin(Session(f, f, self)))

        threading.Thread(target=accept, daemon=True, name="clipbubble-accept").start()

    # Sessions

    def begin(self, s):
        if store.Prefs.get("sync.paused", False) and s.link is None:
            s.close()
            return
        self.sessions.append(s)
        self._hook()
        s.start(Hello(sync.PROTO, self.device, self.name, "linux", VERSION, time.time()), self.db.stamps(including_waiting=False))

    def ended(self, s):
        if s in self.sessions:
            self.sessions.remove(s)
        if s.link:
            s.link.session_ended(s)
        self.publish_status()

    def _hook(self):
        if not self.hooked:
            self.hooked = True
            store.changed = lambda hashes: self.post(lambda: self.broadcast(hashes))

    def broadcast(self, hashes):
        ready = [s for s in self.sessions if s.summarized]
        for h in set(hashes):
            st = self.db.stamp(h)
            if st is None:
                continue
            for s in ready:
                if st.beats(s.known.get(h)):
                    self.send(st, s)

    def send(self, st, s):
        if st.deleted:
            s.known[st.hash] = st
            s.send((sync.TOMBSTONE, st))
            return
        c = self.db.clip(hash=st.hash)
        if c is None or not self.db.allowed(c.kind, c.source):
            return
        e = self.entry(c)
        if e is None:
            return
        s.known[st.hash] = st
        if e.blobs:
            s.fetchable.add(st.hash)
        if not c.origin and time.time() - c.created < 10:
            self.sent_at[st.hash] = time.time()
        s.send((sync.PUT, e))

    def entry(self, c):
        origin = c.origin or self.device
        if c.kind == "text":
            # Only an old version stores more than this. Sent, it would drop the link on every reconnect.
            if len(c.text.encode()) > sync.MAX_TEXT:
                return None
            return Entry(c.hash, c.created, "text", c.text, c.source, origin)
        # Offered before its bytes are here, an item is a dead end for every device after this one.
        if c.remote:
            return None
        if c.kind == "image":
            if not c.file or not os.path.exists(c.file):
                return None
            h = c.hash[2:]
            return Entry(c.hash, c.created, "image", "", c.source, origin, (Blob(h + ".png", os.path.getsize(c.file), h),))
        names = "\n".join(os.path.basename(p) for p in c.paths)
        blobs = tuple(Blob(os.path.basename(p), os.path.getsize(p), "") for p in self.blob_files(c))
        return Entry(c.hash, c.created, "file", names, c.source, origin, blobs)

    def blob_files(self, c):
        if c.kind == "image":
            return [c.file] if c.file else []
        return [p for p in c.paths if os.path.isabs(p) and os.path.isfile(p)]

    # Frames

    def handle(self, frame, s):
        code, p = frame
        if code == sync.HELLO:
            if p.proto != sync.PROTO:
                log(f"{p.name} speaks protocol {p.proto}, this machine {sync.PROTO}")
                if s.link:
                    s.link.mismatch = p.version
                s.close()
                return
            if p.device == self.device:
                s.close()
                return
            old = next((o for o in self.sessions if o is not s and o.peer and o.peer.device == p.device), None)
            if old:
                # A device dialling in again has given up on its old link (an iPhone that slept): the new one replaces it.
                # Otherwise the two ends dialled each other, and the first link stays.
                if old.link or s.link:
                    s.close()
                    return
                old.close()
            s.peer = p
            s.offset = p.clock - time.time()
            if abs(s.offset) > 30:
                log(f"{p.name}'s clock is {int(s.offset)} s off")
            if self.db.save_device(p.device, p.name, p.os):
                self.share((p.device, p.name, p.os), s)
            if p.reads_devices:
                for d in self.db.devices():
                    if d[0] != p.device:
                        s.send((sync.DEVICE, tuple(d)))
            if s.link:
                s.link.hello_received(p)
            log(f"linked with {p.name} ({p.os} {p.version})")
            self.publish_status()
        elif code == sync.SUMMARY:
            if s.peer is None:
                s.close()
                return
            for st in p:
                if st.beats(s.known.get(st.hash)):
                    s.known[st.hash] = st
            s.summarized = True
            mine = sync.reconcile(self.db.stamps(), p)
            for st in sorted(mine, key=lambda x: x.time):
                self.send(st, s)
            log(f"reconciled with {s.peer.name}: sent {len(mine)} of {len(p)} known there")
        elif code == sync.PUT:
            self.received(p, s)
        elif code == sync.TOMBSTONE:
            if not p.deleted:
                s.close()
                return
            if p.beats(s.known.get(p.hash)):
                s.known[p.hash] = p
            if sync.merge(self.db.stamp(p.hash), self.db.exists(p.hash), p) == "delete":
                s.cancel_download(p.hash)
                self.db.tombstone(p)
                self.on_received()
        elif code == sync.FETCH:
            h, index, offset = p
            c = self.db.clip(hash=h)
            # A put on this link is the only way in, so naming a hash or a path reaches nothing else.
            ok = h in s.fetchable and c and not c.remote and self.db.allowed(c.kind, c.source)
            files = self.blob_files(c) if ok else []
            if index >= len(files) or not s.upload(h, index, files[index], offset):
                s.send((sync.CANCEL, h))
            else:
                self.watch_transfers()
        elif code == sync.CHUNK:
            done = s.chunk(*p)
            if done:
                self.finished(done, s)
        elif code == sync.CANCEL:
            s.cancel_upload(p)
            s.cancel_download(p)
        elif code == sync.APPLIED:
            t = self.sent_at.pop(p, None)
            if t:
                log(f"applied on {s.peer.name if s.peer else 'peer'} in {int((time.time() - t) * 1000)} ms")
        elif code == sync.PING:
            s.send((sync.PONG, p))
        elif code == sync.OFFER:
            c = self.db.clip(hash=p)
            if p in s.offered and c and c.remote:
                s.download(self.make_download(s.offered[p], s, False))
                self.watch_transfers()
        elif code == sync.DEVICE:
            if s.peer is None:
                s.close()
                return
            if p[0] not in (self.device, s.peer.device) and self.db.save_device(*p):
                self.share(p, s)
                self.on_received()

    def share(self, device, s):
        """Passes news of a device on to the other links, so a device two hops away still shows its name.
        Only news travels, so names settle after one pass however the devices are linked."""
        for o in self.sessions:
            if o is not s and o.peer and o.peer.reads_devices and o.peer.device != device[0]:
                o.send((sync.DEVICE, device))

    def received(self, e, s):
        st = e.stamp
        if st.beats(s.known.get(e.hash)):
            s.known[e.hash] = st
        now = time.time()
        if e.time < now - store.RETENTION:
            return
        has_row = self.db.exists(e.hash)
        newest = self.db.clips(limit=1)
        fresh = now - (e.time - s.offset) < 10 and e.time > (newest[0].created if newest else 0)
        action = sync.merge(self.db.stamp(e.hash), has_row, st)
        new = False
        if action == "delete":
            return
        if action == "ignore":
            row = self.db.clip(hash=e.hash)
            if not (row and row.remote):
                return
        elif action == "bump":
            self.db.bump(e.hash, e.time)
            new = True
            c = self.db.clip(hash=e.hash)
            if fresh and c and not c.remote:
                self.apply_now(c, s)
        else:
            if not self.db.allowed(e.kind, e.source):
                return
            if e.kind == "file":
                e = dataclasses.replace(e, text="\n".join(safe_file_name(n) for n in e.text.split("\n") if n))
            self.db.insert(e, "" if e.origin == self.device else e.origin)
            new = True
            if e.kind == "text" and fresh:
                self.apply_now(self.db.clip(hash=e.hash), s)
        # Still missing its bytes, whether new here or left over from a link that dropped mid-download.
        c = self.db.clip(hash=e.hash)
        if e.blobs and c and c.remote:
            s.offered[e.hash] = e
            if e.size <= sync.AUTO_FETCH_LIMIT or e.hash in self.wanted:
                s.download(self.make_download(e, s, fresh))
                self.watch_transfers()
        self.on_received()
        if new:
            self.on_new()

    def apply_now(self, c, s):
        self.apply(c)
        s.send((sync.APPLIED, c.hash))

    def finished(self, d, s):
        self.wanted.discard(d.hash)
        s.offered.pop(d.hash, None)
        if d.kind == "image":
            self.db.received(d.hash, file=d.paths[0])
        else:
            self.db.received(d.hash, text="\n".join(d.paths))
        newest = self.db.clips(limit=1)
        if d.apply and newest and newest[0].hash == d.hash:
            self.apply_now(newest[0], s)
        self.on_received()

    def serve_stdio(self):
        """--peer-stdio: one session on stdin and stdout, with no UI. For tests and interop checks."""
        done = threading.Event()

        def go():
            s = Session(os.dup(0), os.dup(1), self)
            s.on_close = done.set
            self.begin(s)

        self.post(go)
        done.wait()


class HostLink:
    """One host from ~/.ssh/config: runs ssh, retries with backoff, turns ssh's errors into a status."""
    POSIX = ("sh -c 'for p in \"$HOME/.local/bin/clipbubble\" \"$HOME/Applications/ClipBubble.app/Contents/MacOS/ClipBubble\" "
             "/Applications/ClipBubble.app/Contents/MacOS/ClipBubble; do [ -x \"$p\" ] && exec \"$p\" --peer; done; "
             "echo CLIPBUBBLE_NOT_INSTALLED >&2; exit 127'")
    WINDOWS = '"%LOCALAPPDATA%\\Programs\\ClipBubble\\ClipBubble.exe" --peer'

    def __init__(self, alias, engine):
        self.alias = alias
        self.engine = engine
        self.proc = None
        self.session = None
        self.stderr = ""
        self.backoff = 1.0
        self.timer = None
        self.stopped = False
        self.mismatch = None
        self.state = ("connecting", "")

    @property
    def windows(self):
        return store.Prefs.get("sync.os." + self.alias) == "windows"

    def connect(self):
        if self.stopped or self.proc or self.alias.startswith("-"):
            return
        self._set(("connecting", ""))
        args = ["ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=15",
                "-o", "ServerAliveCountMax=2", "--", self.alias, self.WINDOWS if self.windows else self.POSIX]
        try:
            self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                         start_new_session=True)
        except OSError:
            self._set(("offline", time.time() + self.backoff))
            self._retry()
            return
        self.stderr = ""
        proc = self.proc

        def read_err():
            for line in proc.stderr:
                if len(self.stderr) < 8192:
                    self.stderr += line.decode("utf-8", "replace")

        def wait():
            proc.wait()
            self.engine.post(lambda: self._exited(proc))

        threading.Thread(target=read_err, daemon=True).start()
        threading.Thread(target=wait, daemon=True).start()
        self.session = Session(os.dup(proc.stdout.fileno()), os.dup(proc.stdin.fileno()), self.engine)
        proc.stdout.close()
        proc.stdin.close()
        self.session.link = self
        self.engine.begin(self.session)

    def hello_received(self, h):
        self.backoff = 1.0
        self.mismatch = None
        self._set(("live", h.name))

    def session_ended(self, s):
        if self.session is s:
            self.session = None
            if self.proc:
                self.proc.terminate()

    def _exited(self, proc):
        if proc is not self.proc:
            return
        was_live = self.state[0] == "live"
        self.proc = None
        if self.session:
            s, self.session = self.session, None
            s.close()
        if self.stopped:
            return
        err = self.stderr
        if not self.windows and ("is not recognized" in err or "CommandNotFoundException" in err):
            store.Prefs.set("sync.os." + self.alias, "windows")
            self.connect()
            return
        if self.mismatch:
            nxt = ("version", self.mismatch)
        elif "CLIPBUBBLE_NOT_INSTALLED" in err or (self.windows and "cannot find the path" in err):
            nxt = ("not-installed", "")
        elif "CLIPBUBBLE_NOT_RUNNING" in err:
            nxt = ("not-running", "")
        elif "Host key verification failed" in err:
            nxt = ("unknown-host", "")
        elif "Permission denied" in err or "Too many authentication failures" in err:
            nxt = ("needs-key", "")
        else:
            nxt = ("offline", time.time() + (1 if was_live else self.backoff))
        if err.strip():
            log(f"{self.alias}: {err.strip()[-300:]}")
        if nxt[0] != "offline":
            self.backoff = max(self.backoff, 60)
        if was_live:
            self.backoff = 1.0
        self._set(nxt)
        self._retry()

    def _retry(self):
        delay = self.backoff
        self.backoff = min(self.backoff * 2, 300)
        self.timer = threading.Timer(delay, lambda: self.engine.post(self.connect))
        self.timer.daemon = True
        self.timer.start()

    def redial(self):
        if not self.stopped and not self.proc:
            if self.timer:
                self.timer.cancel()
            self.backoff = 1.0
            self.connect()

    def stop(self):
        self.stopped = True
        if self.timer:
            self.timer.cancel()
        if self.session:
            self.session.close()
        if self.proc:
            self.proc.terminate()

    def _set(self, state):
        self.state = state
        self.engine.publish_status()


class Session:
    """One peer. Reads frames on a thread, writes on another with control frames ahead of file chunks."""

    def __init__(self, rfd, wfd, engine):
        self.rfd, self.wfd = rfd, wfd
        self.engine = engine
        self.link = None
        self.on_close = lambda: None
        self.peer = None
        self.offset = 0.0
        self.summarized = False
        self.known = {}
        # Items the peer has bytes for that haven't come over yet, with their blob sizes, for Get and Send to.
        self.offered = {}
        # Items sent to the peer with bytes on this link: the only ones it may fetch. `known` won't do, since the
        # peer's summary fills it.
        self.fetchable = set()
        self.reader = sync.FrameReader()
        self.downloads = {}
        self.pending = collections.deque()
        self.cv = threading.Condition()
        self.control = collections.deque()
        self.uploads = collections.deque()
        self.closed = False

    def start(self, hello, summary):
        threading.Thread(target=self._read, daemon=True, name="clipbubble-read").start()
        threading.Thread(target=self._write, daemon=True, name="clipbubble-write").start()
        self.send((sync.HELLO, hello))
        self.send((sync.SUMMARY, summary))

    def _read(self):
        while True:
            try:
                data = os.read(self.rfd, 65536)
            except OSError:
                data = b""
            if not data:
                self.engine.post(self.close)
                if self.rfd != self.wfd:
                    os.close(self.rfd)
                return
            self.engine.post(lambda d=data: self._feed(d))

    def _feed(self, data):
        if self.closed:
            return
        try:
            frames = self.reader.feed(data)
        except sync.Malformed:
            log(f"dropped {self.peer.name if self.peer else 'peer'}: malformed frame")
            self.close()
            return
        for f in frames:
            if self.closed:
                return
            self.engine.handle(f, self)

    def send(self, frame):
        data = sync.encode(frame)
        with self.cv:
            self.control.append(data)
            self.cv.notify()

    def _next(self):
        with self.cv:
            while True:
                if self.closed:
                    return None
                if self.control:
                    return self.control.popleft()
                if self.uploads:
                    u = self.uploads[0]
                    frame = u.next_chunk()
                    if frame is None:
                        self.uploads.popleft()
                        return sync.encode((sync.CANCEL, u.hash))
                    if u.done:
                        self.uploads.popleft()
                    return sync.encode(frame)
                self.cv.wait()

    def _write(self):
        while True:
            data = self._next()
            if data is None:
                os.close(self.wfd)
                return
            try:
                view = memoryview(data)
                while view:
                    n = os.write(self.wfd, view)
                    view = view[n:]
            except OSError:
                self.engine.post(self.close)
                self._next()
                os.close(self.wfd)
                return

    def upload(self, h, index, path, offset):
        try:
            u = Upload(h, index, path, offset)
        except OSError:
            return False
        with self.cv:
            self.uploads.append(u)
            self.cv.notify()
        return True

    def cancel_upload(self, h):
        with self.cv:
            self.uploads = collections.deque(u for u in self.uploads if u.hash != h)

    def is_transferring(self, h):
        with self.cv:
            up = any(u.hash == h for u in self.uploads)
        return up or h in self.downloads or any(d.hash == h for d in self.pending)

    def transfers(self):
        name = self.peer.name if self.peer else "Device"
        out = {d.hash: Transfer(d.done_bytes, d.total, name, False) for d in list(self.downloads.values()) + list(self.pending)}
        with self.cv:
            for u in self.uploads:
                out[u.hash] = Transfer(u.offset, u.total, name, True)
        return out

    def download(self, d):
        if d.hash in self.downloads or any(p.hash == d.hash for p in self.pending):
            return
        if not self.downloads:
            self._begin(d)
        else:
            self.pending.append(d)

    def _begin(self, d):
        self.downloads[d.hash] = d
        self.send((sync.FETCH, (d.hash, d.index, d.received)))

    def cancel_download(self, h):
        self.pending = collections.deque(d for d in self.pending if d.hash != h)
        d = self.downloads.pop(h, None)
        if d:
            d.discard()
            self._start_next()

    def _start_next(self):
        if not self.downloads and self.pending:
            self._begin(self.pending.popleft())

    def chunk(self, h, index, offset, total, data):
        d = self.downloads.get(h)
        if not d or index != d.index or offset != d.received:
            return None
        step = d.write(data, total)
        if step == "more":
            return None
        if step == "failed":
            log(f"couldn't save a file from {d.source}")
            del self.downloads[h]
            self._start_next()
            return None
        if step == "next":
            self.send((sync.FETCH, (h, d.index, 0)))
            return None
        del self.downloads[h]
        self._start_next()
        return d

    def close(self):
        if self.closed:
            return
        # Wake the reader before the writer can close a shared fd: a socket gets shut down; an ssh pipe ends when
        # its link stops ssh.
        try:
            with socket.fromfd(self.rfd, socket.AF_UNIX, socket.SOCK_STREAM) as sock:
                sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        with self.cv:
            self.closed = True
            self.uploads.clear()
            self.cv.notify_all()
        # Partial files stay, so the next link resumes them from where this one stopped.
        for d in self.downloads.values():
            d.suspend()
        self.downloads.clear()
        self.engine.ended(self)
        self.on_close()


class Upload:
    def __init__(self, h, index, path, offset):
        self.hash, self.index = h, index
        self.f = open(path, "rb")
        self.total = os.fstat(self.f.fileno()).st_size
        if not 0 <= offset <= self.total:
            raise OSError("bad offset")
        self.f.seek(offset)
        self.offset = offset
        self.sent_empty = False

    @property
    def done(self):
        return self.offset >= self.total and (self.total > 0 or self.sent_empty)

    def next_chunk(self):
        n = min(sync.CHUNK_SIZE, self.total - self.offset)
        data = self.f.read(n) if n > 0 else b""
        if len(data) != n:
            return None
        frame = (CHUNK, (self.hash, self.index, self.offset, self.total, data))
        self.offset += n
        if n == 0:
            self.sent_empty = True
        if self.done:
            self.f.close()
        return frame


class Transfer(collections.namedtuple("Transfer", "done total peer sending")):
    @property
    def fraction(self):
        return min(1.0, self.done / self.total) if self.total > 0 else 0.0


class Download:
    def __init__(self, h, kind, names, shas, sizes, source, apply):
        self.hash, self.kind, self.names, self.shas, self.sizes = h, kind, names, shas, sizes
        self.source, self.apply = source, apply
        self.index = 0
        self.received = 0
        self.paths = []
        self.f = None
        # A partial first file from a link that dropped: carry on from its end.
        try:
            size = self.temp.stat().st_size
            if size <= (sizes[0] if sizes else 0):
                self.received = size
        except OSError:
            pass

    @property
    def total(self):
        return sum(self.sizes)

    @property
    def done_bytes(self):
        return sum(self.sizes[:self.index]) + self.received

    @property
    def temp(self):
        return DIR / "incoming" / f"{sha(self.hash)}-{self.index}"

    def write(self, data, total):
        if self.index >= len(self.names) or self.received + len(data) > total:
            return "failed"
        if self.f is None:
            self.temp.parent.mkdir(parents=True, exist_ok=True)
            fd = os.open(self.temp, os.O_RDWR | os.O_CREAT, 0o600)
            self.f = os.fdopen(fd, "r+b")
            self.f.truncate(self.received)
            self.f.seek(self.received)
        self.f.write(data)
        self.received += len(data)
        if self.received != total:
            return "more"
        self.f.close()
        self.f = None
        dest = self._place()
        if dest is None:
            return "failed"
        self.paths.append(dest)
        self.index += 1
        self.received = 0
        return "next" if self.index < len(self.names) else "done"

    def _place(self):
        if self.kind == "image":
            data = self.temp.read_bytes()
            want = self.shas[self.index] if self.index < len(self.shas) else ""
            if want and sha(data) != want:
                self.temp.unlink(missing_ok=True)
                return None
            dest = IMAGES / (sha(data) + ".png")
            os.replace(self.temp, dest)
            return str(dest)
        folder = received_folder(self.source)
        folder.mkdir(parents=True, exist_ok=True)
        dest = unique_path(folder, safe_file_name(self.names[self.index]))
        shutil.move(str(self.temp), dest)
        return str(dest)

    def suspend(self):
        if self.f:
            self.f.close()
            self.f = None

    def discard(self):
        self.suspend()
        self.temp.unlink(missing_ok=True)


def ssh_hosts():
    """Host aliases from ~/.ssh/config and the files it includes, skipping patterns."""
    import glob
    ssh = Path.home() / ".ssh"
    seen, visited = [], set()

    def read(path):
        if path in visited:
            return
        visited.add(path)
        try:
            text = Path(path).read_text()
        except (OSError, UnicodeDecodeError):
            return
        for line in text.splitlines():
            parts = line.replace("=", " ").split()
            if not parts:
                continue
            key = parts[0].lower()
            if key == "host":
                for h in parts[1:]:
                    if not any(c in h for c in "*?") and not h.startswith("!") and h not in seen:
                        seen.append(h)
            elif key == "include":
                for pattern in parts[1:]:
                    p = os.path.expanduser(pattern)
                    for f in sorted(glob.glob(p if p.startswith("/") else str(ssh / p))):
                        read(f)

    read(str(ssh / "config"))
    return seen


def state_text(state):
    kind, arg = state
    if kind == "live":
        return f"Linked with {arg}"
    if kind == "offline":
        s = max(0, int(arg - time.time())) if arg else 0
        return f"Offline, retrying in {s} s" if 0 < s < 60 else f"Offline, retrying in {s // 60} min" if s else "Offline"
    return {"connecting": "Connecting…", "needs-key": "Needs SSH key login",
            "unknown-host": "Connect once in a terminal to trust this host",
            "not-installed": "ClipBubble isn't installed there", "not-running": "ClipBubble isn't running there",
            "version": f"Update needed (it has {arg})"}.get(kind, kind)


def relay():
    """clipbubble --peer, run by ssh here: pipes ssh's stdio to the running app, starting it if needed."""
    import sys
    path = store.socket_path()

    def dial():
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            s.connect(path)
            return s
        except OSError:
            s.close()
            return None

    s = dial()
    if s is None:
        try:
            started = subprocess.run(["systemctl", "--user", "start", "clipbubble.service"], capture_output=True).returncode == 0
        except OSError:
            started = False
        if not started:  # no systemd here: start the app on its own
            subprocess.Popen([sys.executable, "-m", "clipbubble"], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, start_new_session=True)
        for _ in range(40):
            time.sleep(0.25)
            s = dial()
            if s:
                break
    if s is None:
        sys.stderr.write("CLIPBUBBLE_NOT_RUNNING\n")
        sys.exit(3)

    def pump(src, dst, after):
        while True:
            try:
                d = os.read(src, 65536)
            except OSError:
                d = b""
            if not d:
                break
            try:
                view = memoryview(d)
                while view:
                    view = view[os.write(dst, view):]
            except OSError:
                break
        after()

    t = threading.Thread(target=pump, args=(0, s.fileno(), lambda: s.shutdown(socket.SHUT_WR)), daemon=True)
    t.start()
    pump(s.fileno(), 1, lambda: None)
    os._exit(0)
