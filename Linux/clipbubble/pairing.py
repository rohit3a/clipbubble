"""Links an iPhone without a terminal, as the Mac does (Sources/Pairing.swift). The QR code carries this machine's
addresses and a one-time code. The phone sends its SSH key back over the local network, and the key goes into
~/.ssh/authorized_keys allowed to run only ClipBubble's relay."""
import getpass
import hmac
import json
import os
import re
import secrets
import socket
import subprocess
import threading
from pathlib import Path
from urllib.parse import quote, urlencode

# The launcher install.sh writes, which ssh runs with --peer.
LAUNCHER = Path.home() / ".local/bin/clipbubble"
SSH_DIR = Path.home() / ".ssh"
# A fixed port, so one firewall rule covers pairing. Any free port does if another app has it.
PORT = 47821
# Interfaces a phone can't reach: containers, bridges and VMs on this machine.
_VIRTUAL = re.compile(r"^(lo|docker|br-|veth|virbr|vmnet|vboxnet|podman|cni|flannel|lxc|lxd)")


class Server:
    """Answers one phone, then stops. `on_paired(name)` and `on_expired()`, after ten quiet minutes, run on the
    server's thread."""

    def __init__(self, name, on_paired, on_expired=lambda: None, timeout=600):
        self.code = secrets.token_hex(16)
        self.name = name
        self.on_paired = on_paired
        self.on_expired = on_expired
        try:
            self.sock = socket.create_server(("0.0.0.0", PORT))
        except OSError:
            self.sock = socket.create_server(("0.0.0.0", 0))
        self.sock.settimeout(timeout)
        self.port = self.sock.getsockname()[1]
        threading.Thread(target=self._serve, daemon=True, name="clipbubble-pairing").start()

    def link(self):
        q = {"u": getpass.getuser(), "h": ",".join(addresses()), "p": self.port, "c": self.code, "n": self.name}
        return "clipbubble://pair?" + urlencode(q, quote_via=quote, safe=",")

    def stop(self):
        try:
            self.sock.close()
        except OSError:
            pass

    def _serve(self):
        while True:
            try:
                c, _ = self.sock.accept()
            except socket.timeout:
                self.stop()
                self.on_expired()
                return
            except OSError:
                return
            with c:
                c.settimeout(10)
                if self._answer(c):
                    self.stop()
                    return

    def _answer(self, c):
        buf = b""
        try:
            while b"\n" not in buf and len(buf) <= 8192:
                d = c.recv(4096)
                if not d:
                    return False
                buf += d
            req = json.loads(buf.split(b"\n", 1)[0])
        except (OSError, ValueError):
            return False
        reply, paired = {"ok": False}, False
        if isinstance(req, dict) and hmac.compare_digest(str(req.get("code", "")).encode(), self.code.encode()) \
                and isinstance(req.get("key"), str) and isinstance(req.get("name"), str):
            try:
                authorize(req["key"], req["name"])
                reply = {"ok": True, "user": getpass.getuser(), "name": self.name, "hostKey": host_key() or ""}
                paired = True
            except (OSError, ValueError) as e:
                reply["error"] = str(e)
        try:
            c.sendall(json.dumps(reply).encode() + b"\n")
        except OSError:
            pass
        if paired:
            self.on_paired(req["name"])
        return paired


def authorize(key, name):
    """Adds the phone's key, limited to running this app's relay: no shell, no forwarding, no terminal."""
    parts = key.split(" ")
    if len(parts) != 2 or parts[0] != "ssh-ed25519" or not re.fullmatch(r"[A-Za-z0-9+/=]+", parts[1]):
        raise ValueError("That isn't an iPhone key ClipBubble made.")
    exe = str(LAUNCHER)
    if "'" in exe or '"' in exe:
        raise ValueError("ClipBubble's folder has a quote in its name.")
    label = "".join(ch for ch in name if ch.isalnum() or ch in " -")[:40]
    SSH_DIR.mkdir(mode=0o700, exist_ok=True)
    f = SSH_DIR / "authorized_keys"
    # Pairing the same phone again replaces its line.
    lines = [line for line in _lines(f) if parts[1].encode() not in line]
    while lines and lines[-1] == b"":
        lines.pop()
    lines.append(f"restrict,command=\"'{exe}' --peer\" {key} ClipBubble on {label}".encode())
    _write(f, lines)


def phones():
    """The phones linked to this computer, as (name, key)."""
    out = []
    for line in _lines(SSH_DIR / "authorized_keys"):
        text = line.decode(errors="replace")
        head, _, name = text.partition(" ClipBubble on ")
        fields = head.split(" ")
        if name and text.startswith("restrict,command=") and len(fields) >= 2 and fields[-2] == "ssh-ed25519":
            out.append((name, fields[-1]))
    return out


def unlink(key):
    """Takes a phone's line out, leaving every other line as it was."""
    f = SSH_DIR / "authorized_keys"
    lines = [line for line in _lines(f) if key.encode() not in line]
    while lines and lines[-1] == b"":
        lines.pop()
    _write(f, lines)


def _lines(f):
    """The file holds keys that aren't ClipBubble's, so lines stay bytes and every line it doesn't own stays exact."""
    return f.read_bytes().split(b"\n") if f.exists() else []


def _write(f, lines):
    real = Path(os.path.realpath(f))  # through a symlink, so a key file kept in dotfiles stays linked
    tmp = real.with_name(real.name + ".clipbubble")
    with os.fdopen(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "wb") as out:
        out.write(b"\n".join(lines) + b"\n")
    os.replace(tmp, real)


def host_key():
    """The key sshd shows clients, so the phone checks it's talking to this machine from the first connection."""
    try:
        return " ".join(Path("/etc/ssh/ssh_host_ed25519_key.pub").read_text().split()[:2])
    except OSError:
        return None


def firewall_hint():
    """How to let the phone in, when a firewall that drops it by default is on. Its rules need root to read, so this
    can't tell whether they already allow it."""
    try:
        if "ENABLED=yes" in Path("/etc/ufw/ufw.conf").read_text():
            return f"If the iPhone can't connect, let it through your firewall once: sudo ufw allow 22,{PORT}/tcp"
    except OSError:
        pass
    try:
        firewalld = subprocess.run(["systemctl", "is-active", "--quiet", "firewalld"], capture_output=True).returncode == 0
    except OSError:
        firewalld = False
    if firewalld:
        return (f"If the iPhone can't connect, let it through your firewall once: sudo firewall-cmd --permanent "
                f"--add-service=ssh --add-port={PORT}/tcp && sudo firewall-cmd --reload")
    return None


def ssh_server_on():
    """sshd is what the phone's SSH connection reaches."""
    try:
        socket.create_connection(("127.0.0.1", 22), timeout=0.5).close()
        return True
    except OSError:
        return False


def addresses():
    """The .local name first when Avahi answers for it, since it survives a new IP address; then the IPv4 address of
    every interface a phone could reach."""
    names = [socket.gethostname().split(".")[0] + ".local"] if os.path.exists("/run/avahi-daemon/socket") else []
    try:
        text = subprocess.run(["ip", "-4", "-o", "addr", "show", "up"], capture_output=True, text=True, timeout=2).stdout
    except (OSError, subprocess.SubprocessError):
        text = ""
    ips = []
    for line in text.splitlines():
        f = line.split()
        if len(f) >= 4 and not _VIRTUAL.match(f[1]):
            ip = f[3].split("/")[0]
            if not ip.startswith("169.254.") and ip not in ips:
                ips.append(ip)
    if not ips:
        # No `ip` tool: the address the default route leaves from. Connecting a UDP socket sends nothing.
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
                s.connect(("192.0.2.1", 9))
                ips.append(s.getsockname()[0])
        except OSError:
            pass
    return names + ips
