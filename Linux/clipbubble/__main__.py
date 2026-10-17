"""clipbubble: the app (default), --peer (run by ssh), --peer-stdio (tests), --mcp, --version."""
import os
import signal
import sys
import time


def run_app():
    from .engine import Engine, log
    from .store import DB
    try:
        from gi.repository import GLib
    except ImportError:
        # A server with no desktop: history still syncs, and agents there still see it.
        engine = Engine()
        engine.start()
        while True:
            DB().prune()
            time.sleep(3600)

    from . import board
    from .capture import Capture
    from .shell import Shell

    shell = Shell()
    engine = Engine()
    engine.apply = lambda c: board.write(shell, c) or log(f"couldn't put {c.hash[:10]} on the clipboard")
    Capture(shell)
    engine.start()

    def prune():
        DB().prune()
        return True

    prune()
    GLib.timeout_add_seconds(3600, prune)
    loop = GLib.MainLoop()
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGTERM, loop.quit)
    loop.run()


def run_peer_stdio():
    from .engine import Engine, log
    engine = Engine()
    applied = os.path.join(os.environ.get("CLIPBUBBLE_HOME", "."), "applied.txt")

    def apply(c):
        with open(applied, "a") as f:
            f.write(c.hash + "\n")

    engine.apply = apply
    engine.serve_stdio()


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
    elif "--mcp" in args:
        from .mcp import Server
        Server().run()
    else:
        run_app()


main()
