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

    def test_a_byte_that_isnt_text_keeps_every_key(self):
        self.ssh.mkdir(mode=0o700)
        keys = self.ssh / "authorized_keys"
        old = b"ssh-ed25519 AAAAone caf\xe9 laptop\nssh-ed25519 AAAAtwo work\n"
        keys.write_bytes(old)
        pairing.authorize(KEY, "iPhone")
        self.assertTrue(keys.read_bytes().startswith(old))
        self.assertEqual(keys.read_bytes().count(b"\n"), 3)

    def test_crlf_lines_survive_pairing_again(self):
        self.ssh.mkdir(mode=0o700)
        keys = self.ssh / "authorized_keys"
        mine = f"restrict,command=\"'old' --peer\" {KEY} ClipBubble on iPhone".encode()
        keys.write_bytes(b"ssh-ed25519 AAAAone laptop\r\n" + mine + b"\r\nssh-ed25519 AAAAtwo work\r\n")
        pairing.authorize(KEY, "iPhone")
        lines = keys.read_bytes().split(b"\n")
        self.assertEqual(lines[:2], [b"ssh-ed25519 AAAAone laptop\r", b"ssh-ed25519 AAAAtwo work\r"])
        self.assertEqual(len(lines), 4)
        self.assertEqual(sum(KEY.encode() in line for line in lines), 1)

    def test_unlinking_removes_only_that_phone(self):
        self.ssh.mkdir(mode=0o700, exist_ok=True)
        f = self.ssh / "authorized_keys"
        f.write_bytes(b"ssh-ed25519 AAAAone laptop\r\n")
        pairing.authorize(KEY, "Rohit's iPhone")
        self.assertEqual([n for n, _ in pairing.phones()], ["Rohits iPhone"])
        pairing.unlink(pairing.phones()[0][1])
        self.assertEqual(pairing.phones(), [])
        self.assertEqual(f.read_bytes(), b"ssh-ed25519 AAAAone laptop\r\n")

    def test_no_final_newline_keeps_the_last_key_whole(self):
        self.ssh.mkdir(mode=0o700)
        keys = self.ssh / "authorized_keys"
        keys.write_bytes(b"ssh-ed25519 AAAAone laptop")
        pairing.authorize(KEY, "iPhone")
        self.assertEqual(keys.read_bytes().split(b"\n")[0], b"ssh-ed25519 AAAAone laptop")

    def test_a_symlink_stays_a_symlink(self):
        self.ssh.mkdir(mode=0o700)
        target = self.ssh.parent / "dotfiles-keys"
        target.write_bytes(b"ssh-ed25519 AAAAone laptop\n")
        keys = self.ssh / "authorized_keys"
        keys.symlink_to(target)
        pairing.authorize(KEY, "iPhone")
        self.assertTrue(keys.is_symlink())
        self.assertEqual(target.read_bytes().count(b"\n"), 2)
        self.assertIn(b"AAAAone", target.read_bytes())
        self.assertEqual(stat.S_IMODE(os.stat(target).st_mode), 0o600)

    @unittest.skipIf(os.geteuid() == 0, "root reads anything")
    def test_a_file_it_cant_read_is_left_alone(self):
        self.ssh.mkdir(mode=0o700)
        keys = self.ssh / "authorized_keys"
        keys.write_bytes(b"ssh-ed25519 AAAAone laptop\n")
        keys.chmod(0)
        self.addCleanup(keys.chmod, 0o600)
        with self.assertRaises(OSError):
            pairing.authorize(KEY, "iPhone")
        keys.chmod(0o600)
        self.assertEqual(keys.read_bytes(), b"ssh-ed25519 AAAAone laptop\n")

    def test_refuses_anything_but_an_ed25519_key(self):
        for bad in ("ssh-rsa AAAA", KEY + " extra", "ssh-ed25519 AAAA\"; rm -rf ~", "ssh-ed25519 AAAA\nssh-ed25519 BBBB"):
            with self.assertRaises(ValueError):
                pairing.authorize(bad, "iPhone")
        self.assertFalse((self.ssh / "authorized_keys").exists())
