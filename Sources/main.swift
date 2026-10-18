import AppKit

// A linked device can vanish mid-write; that should end its link, not the app.
signal(SIGPIPE, SIG_IGN)

if CommandLine.arguments.contains("--peer") { PeerRelay.run() }
if CommandLine.arguments.contains("--peer-stdio") {
    SyncEngine.shared.apply = { c in SyncLog.write("would apply \(c.hash)") }
    SyncEngine.shared.serveStdio()
    dispatchMain()
}
if CommandLine.arguments.contains("--pair") {
    // The same pairing as Settings, from a terminal: prints the link the QR code would hold.
    let p = PairingServer()
    p.onReady = { print($0.absoluteString); fflush(stdout) }
    p.onPaired = { print("Linked \($0)"); exit(0) }
    p.start()
    dispatchMain()
}
if CommandLine.arguments.contains("--mcp") {
    MCP.run()
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
