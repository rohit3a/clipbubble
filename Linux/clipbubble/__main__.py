"""clipbubble: the app (default), --pair (link an iPhone), --peer (run by ssh), --peer-stdio (tests), --mcp, --version."""
import os
import signal
import sys
import time


def has_display():
    try:
        import gi
        gi.require_version("Gtk", "4.0")
        gi.require_version("Gdk", "4.0")
        from gi.repository import Gdk, Gtk
    except (ImportError, ValueError):
        return None   # no GTK at all
    # PyGObject's init_check says True even when it found no display.
    Gtk.init_check()
    return Gdk.Display.get_default() is not None


def desktop_appeared():
    """The Wayland socket a compositor is answering on, if any. One left behind by a crash refuses the connection."""
    import socket
    run = os.environ.get("XDG_RUNTIME_DIR", "")
    try:
        names = sorted(n for n in os.listdir(run) if n.startswith("wayland-") and not n.endswith(".lock"))
    except OSError:
        return None
    for n in names:
        with socket.socket(socket.AF_UNIX) as s:
            try:
                s.connect(os.path.join(run, n))
                return n
            except OSError:
                pass
    return None


def run_app():
    from .engine import Engine
    from .store import DB
    gui = has_display()
    if gui:
        from .ui import run
        run()
        return
    # No desktop: a server, or the login screen after logging out. History still syncs and agents still see it;
    # once someone logs in, start over as the full app.
    engine = Engine()
    engine.start()
    last_prune = 0
    while True:
        if time.time() - last_prune > 3600:
            DB().prune()
            last_prune = time.time()
        time.sleep(5)
        display = gui is False and desktop_appeared()
        if display:
            os.environ["WAYLAND_DISPLAY"] = display
            os.execv(sys.executable, [sys.executable, "-m", "clipbubble"])


def run_peer_stdio():
    from .engine import Engine, log
    engine = Engine()
    applied = os.path.join(os.environ.get("CLIPBUBBLE_HOME", "."), "applied.txt")

    def apply(c):
        with open(applied, "a") as f:
            f.write(c.hash + "\n")

    engine.apply = apply
    engine.serve_stdio()


def run_pair():
    """Link iPhone from a terminal: for a machine without a desktop, or one you're logged in to over SSH."""
    import socket
    import threading
    from . import pairing, qr
    if not pairing.ssh_server_on():
        print("Start the SSH server first, so the iPhone can reach this machine. "
              "On Ubuntu or Debian: sudo apt install openssh-server")
        sys.exit(1)
    done, result = threading.Event(), []
    server = pairing.Server(os.environ.get("CLIPBUBBLE_DEVICE_NAME") or socket.gethostname(),
                            lambda name: (result.append(name), done.set()), on_expired=done.set)
    link = server.link()
    print(qr.terminal(link))
    print("Scan this with the iPhone's Camera, or in ClipBubble on the iPhone. It needs the same Wi-Fi as this machine.")
    print(f"Or open this link on the iPhone: {link}")
    hint = pairing.firewall_hint()
    if hint:
        print(hint)
    sys.stdout.flush()
    try:
        done.wait()
    except KeyboardInterrupt:
        server.stop()
        sys.exit(130)
    if not result:
        print("That code expired. Run clipbubble --pair again for a new one.")
        sys.exit(1)
    print(f"Linked {result[0]}. It syncs whenever ClipBubble is open there.")


def main():
    args = sys.argv[1:]
    if "--version" in args:
        from .engine import VERSION
        print(VERSION)
    elif "--peer" in args:
        from .engine import relay
        relay()
    elif "--peer-stdio" in args:
        run_peer_stdio()
    elif "--pair" in args:
        run_pair()
    elif "--mcp" in args:
        from .mcp import Server
        Server().run()
    else:
        run_app()


main()
