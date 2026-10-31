import Foundation

/// Installs or updates ClipBubble on a host from this Mac: the Linux app ships inside this app and goes over SSH.
enum RemoteSetup {
    enum Result: Equatable {
        case ready
        case needsRelogin
        case oldGNOME
        case headless
        case needsPython
        case otherOS(String)
        case failed(String)

        var text: String {
            switch self {
            case .ready: return "Installed. Copies sync both ways now."
            case .needsRelogin: return "Installed. Sending works now. Log out and back in there once so it can see copies made there."
            case .oldGNOME: return "Installed. Sending works now, but seeing copies made there needs a newer GNOME."
            case .headless: return "Installed. History syncs; there's no desktop clipboard there."
            case .needsPython: return "Needs Python 3.8 or newer there."
            case .otherOS(let os): return "That's a \(os) machine: install ClipBubble there from the releases page."
            case .failed(let why): return "Couldn't set it up: \(why)"
            }
        }
    }

    static let releases = URL(string: "https://github.com/rohit3a/clipbubble/releases/latest")!

    static var bundle: URL? { Bundle.main.resourceURL?.appendingPathComponent("linux") }

    /// The Linux app version inside this app, for spotting hosts that need an update.
    static var bundledVersion: String? {
        bundle.flatMap { try? String(contentsOf: $0.appendingPathComponent("VERSION"), encoding: .utf8) }?
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func isOutdated(_ h: Hello) -> Bool {
        guard h.os == "linux", let v = bundledVersion else { return false }
        return h.version.compare(v, options: .numeric) == .orderedAscending
    }

    static func run(_ alias: String, progress: @escaping (String) -> Void, done: @escaping (Result) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let r = install(alias, progress: { s in DispatchQueue.main.async { progress(s) } })
            SyncLog.write("setup \(alias): \(r)")
            DispatchQueue.main.async { done(r) }
        }
    }

    private static func ssh(_ alias: String, _ command: String) -> Process {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ssh")
        p.arguments = ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "--", alias, command]
        return p
    }

    private static func install(_ alias: String, progress: (String) -> Void) -> Result {
        guard !alias.hasPrefix("-"), let bundle, FileManager.default.fileExists(atPath: bundle.path) else {
            return .failed("this copy of ClipBubble has no Linux app inside")
        }
        progress("Checking \(alias)…")
        let probe = ssh(alias, "uname -s")
        let out = Pipe(), err = Pipe()
        probe.standardOutput = out
        probe.standardError = err
        do { try probe.run() } catch { return .failed(error.localizedDescription) }
        let os = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        let probeErr = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        probe.waitUntilExit()
        if probeErr.contains("is not recognized") || probeErr.contains("CommandNotFoundException") { return .otherOS("Windows") }
        if os == "Darwin" { return .otherOS("Mac") }
        guard os == "Linux" else {
            return .failed(probeErr.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "\n").last.map(String.init) ?? "no answer")
        }

        progress("Installing on \(alias)…")
        let tar = Process()
        tar.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        tar.arguments = ["-czf", "-", "--no-xattrs", "--exclude", "__pycache__", "--exclude", "tests", "-C", bundle.path, "."]
        var env = ProcessInfo.processInfo.environment
        env["COPYFILE_DISABLE"] = "1"
        tar.environment = env
        let stream = Pipe()
        tar.standardOutput = stream
        let remote = ssh(alias, #"d=$(mktemp -d) && tar xzf - -C "$d" && sh "$d/install.sh"; s=$?; rm -rf "$d"; exit $s"#)
        let rout = Pipe(), rerr = Pipe()
        remote.standardInput = stream
        remote.standardOutput = rout
        remote.standardError = rerr
        do { try tar.run(); try remote.run() } catch { return .failed(error.localizedDescription) }
        let said = String(decoding: rout.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        let errs = String(decoding: rerr.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        tar.waitUntilExit()
        remote.waitUntilExit()
        if said.contains("CLIPBUBBLE_NEEDS_PYTHON") { return .needsPython }
        if said.contains("CLIPBUBBLE_READY") { return .ready }
        if said.contains("CLIPBUBBLE_NEEDS_RELOGIN") { return .needsRelogin }
        if said.contains("CLIPBUBBLE_OLD_GNOME") { return .oldGNOME }
        if said.contains("CLIPBUBBLE_HEADLESS") { return .headless }
        let last = (errs + said).trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "\n").last.map(String.init)
        return .failed(last ?? "the installer exited with \(remote.terminationStatus)")
    }
}
