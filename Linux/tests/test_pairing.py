import json
import os
import socket
import stat
import tempfile
import threading
import unittest
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from clipbubble import pairing, qr

FIXTURES = Path(__file__).resolve().parents[2] / "Tests/fixtures/qr/codes.json"
KEY = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIPqR3mUOcskK0AMMwJekuAgZW350J/juYdkvePQaexfg"


class QR(unittest.TestCase):
    def test_matches_codes_a_phone_read(self):
        # Each fixture was decoded by Apple's Vision framework when it was made, and the Windows encoder matches it too.
        for f in json.loads(FIXTURES.read_text()):
            got = ["".join("1" if d else "0" for d in row) for row in qr.encode(f["text"])]
            self.assertEqual(got, f["rows"], f["text"][:20])

    def test_terminal_has_a_quiet_zone(self):
        lines = qr.terminal("HELLO").split("\n")
        self.assertEqual(len(lines), (21 + 8 + 1) // 2)
        self.assertTrue(all(line.startswith("\033[30;107m    ") for line in lines))


class Pairing(unittest.TestCase):
    def setUp(self):
        self.ssh = Path(tempfile.mkdtemp()) / ".ssh"
        self.saved, pairing.SSH_DIR = pairing.SSH_DIR, self.ssh
        self.paired = []
        self.done = threading.Event()
        self.server = pairing.Server("ROG", lambda name: (self.paired.append(name), self.done.set()))

    def tearDown(self):
        self.server.stop()
        pairing.SSH_DIR = self.saved

    def ask(self, req):
        with socket.create_connection(("127.0.0.1", self.server.port), timeout=5) as c:
            c.sendall(json.dumps(req).encode() + b"\n")
            return json.loads(c.makefile().readline())

    def test_link_says_where_and_how(self):
        q = parse_qs(urlparse(self.server.link()).query)
        self.assertEqual(urlparse(self.server.link()).netloc, "pair")
        self.assertEqual(q["p"], [str(self.server.port)])
        self.assertEqual(q["c"], [self.server.code])
        self.assertEqual(q["n"], ["ROG"])
        self.assertTrue(q["h"][0])

    def test_wrong_code_adds_nothing(self):
        self.assertEqual(self.ask({"code": "0" * 32, "key": KEY, "name": "iPhone"}), {"ok": False})
        self.assertFalse((self.ssh / "authorized_keys").exists())
        self.assertEqual(self.paired, [])

    def test_pairs_once_with_a_restricted_key(self):
        reply = self.ask({"code": self.server.code, "key": KEY, "name": "Rohit's iPhone"})
        self.assertTrue(reply["ok"])
        self.assertEqual(reply["name"], "ROG")
        self.assertTrue(self.done.wait(5))
        self.assertEqual(self.paired, ["Rohit's iPhone"])
        f = self.ssh / "authorized_keys"
        self.assertEqual(f.read_text(), f"restrict,command=\"'{pairing.LAUNCHER}' --peer\" {KEY} ClipBubble on Rohits iPhone\n")
        self.assertEqual(stat.S_IMODE(os.stat(f).st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(os.stat(self.ssh).st_mode), 0o700)
        with self.assertRaises(OSError):
            self.ask({"code": self.server.code, "key": KEY, "name": "again"})

    def test_pairing_again_replaces_the_line_and_keeps_others(self):
        self.ssh.mkdir(mode=0o700)
        (self.ssh / "authorized_keys").write_text("ssh-ed25519 AAAAother laptop\n")
        pairing.authorize(KEY, "iPhone")
        pairing.authorize(KEY, "iPhone")
        lines = (self.ssh / "authorized_keys").read_text().splitlines()
        self.assertEqual(lines[0], "ssh-ed25519 AAAAother laptop")
        self.assertEqual(len(lines), 2)

    def test_refuses_anything_but_an_ed25519_key(self):
        for bad in ("ssh-rsa AAAA", KEY + " extra", "ssh-ed25519 AAAA\"; rm -rf ~", "ssh-ed25519 AAAA\nssh-ed25519 BBBB"):
            with self.assertRaises(ValueError):
                pairing.authorize(bad, "iPhone")
        self.assertFalse((self.ssh / "authorized_keys").exists())
