import AppKit
import Security
import SwiftUI

/// Updates ClipBubble from its GitHub releases: checks now and then, and on request downloads the new app,
/// makes sure it's ClipBubble signed the same way, swaps it in place and reopens.
@MainActor
final class Updater: ObservableObject {
    static let shared = Updater()

    struct Release: Equatable {
        let version: String
        let zip: URL
        let page: URL
    }

    enum State: Equatable {
        case idle
        case checking
        case current
        case available(Release)
        case installing(String)
        case failed(String)
    }

    @Published private(set) var state = State.idle
    @Published var automatic = UserDefaults.standard.object(forKey: "updates.automatic") as? Bool ?? true {
        didSet { UserDefaults.standard.set(automatic, forKey: "updates.automatic") }
    }

    nonisolated static let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    nonisolated static let releases = URL(string: "https://github.com/rohit3a/clipbubble/releases/latest")!
    private nonisolated static let latest = URL(string: "https://api.github.com/repos/rohit3a/clipbubble/releases/latest")!

    /// An ad hoc build can't prove the update came from the same developer, and macOS treats it as a new app.
    static let signedByTeam: Bool = {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(Bundle.main.bundleURL as CFURL, [], &code) == errSecSuccess, let code else { return false }
        return teamID(code) != nil
    }()

    func start() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) { [weak self] in self?.check(quietly: true) }
        Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.check(quietly: true) }
        }
    }

    /// A background check stays quiet when it can't reach GitHub, and leaves a found update or an install alone.
    func check(quietly: Bool = false) {
        switch state {
        case .checking, .installing: return
        case .available where quietly: return
        default: break
        }
        if quietly && !automatic { return }
        if !quietly { state = .checking }
        Task {
            do {
                let r = try await Self.fetchLatest()
                state = Self.isNewer(r.version, than: Self.current) ? .available(r) : .current
            } catch {
                if !quietly { state = .failed("Couldn't check for updates: \(error.localizedDescription)") }
            }
        }
    }

    func install(_ r: Release) {
        if case .installing = state { return }
        state = .installing("Downloading \(r.version)…")
        let app = Bundle.main.bundleURL
        Task {
            do {
                try await Self.install(r, over: app) { s in Task { @MainActor in Updater.shared.state = .installing(s) } }
                state = .installing("Reopening…")
                Self.reopen(app)
            } catch {
                state = .failed(error.localizedDescription)
            }
        }
    }

    /// `ClipBubble --update`: the same update from a terminal. The running app keeps the old version until it reopens.
    nonisolated static func runFromTerminal() -> Never {
        Task {
            do {
                let r = try await fetchLatest()
                guard isNewer(r.version, than: current) else { print("ClipBubble \(current) is up to date."); exit(0) }
                print("Updating ClipBubble \(current) to \(r.version)…")
                try await install(r, over: Bundle.main.bundleURL) { print($0) }
                print("Updated to \(r.version). Quit and reopen ClipBubble to use it.")
                exit(0)
            } catch {
                print("Update failed: \(error.localizedDescription)")
                exit(1)
            }
        }
        dispatchMain()
    }

    nonisolated static func isNewer(_ a: String, than b: String) -> Bool { a.compare(b, options: .numeric) == .orderedDescending }

    nonisolated static func parse(_ data: Data) throws -> Release {
        guard let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = o["tag_name"] as? String,
              let page = (o["html_url"] as? String).flatMap(URL.init(string:)),
              let assets = o["assets"] as? [[String: Any]],
              let zip = (assets.first { $0["name"] as? String == "ClipBubble.zip" }?["browser_download_url"] as? String).flatMap(URL.init(string:))
        else { throw UpdateError("the latest release has no Mac download") }
        return Release(version: tag.hasPrefix("v") ? String(tag.dropFirst()) : tag, zip: zip, page: page)
    }

    private nonisolated static func fetchLatest() async throws -> Release {
        var req = URLRequest(url: latest, timeoutInterval: 20)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let (data, resp) = try await URLSession.shared.data(for: req)
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else { throw UpdateError("GitHub answered \(status)") }
        return try parse(data)
    }

    private nonisolated static func install(_ r: Release, over app: URL, progress: @escaping @Sendable (String) -> Void) async throws {
        guard !app.path.contains("/AppTranslocation/") else {
            throw UpdateError("Move ClipBubble into your Applications folder, open it from there, then update.")
        }
        let folder = app.deletingLastPathComponent()
        guard FileManager.default.isWritableFile(atPath: folder.path) else {
            throw UpdateError("ClipBubble can't replace itself in \(folder.path). Download \(r.version) from the releases page instead.")
        }
        let (zip, resp) = try await URLSession.shared.download(for: URLRequest(url: r.zip, timeoutInterval: 60))
        defer { try? FileManager.default.removeItem(at: zip) }
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else { throw UpdateError("the download failed (\(status))") }

        progress("Checking the download…")
        // On the app's own volume, so the swap below is a rename.
        let work = try FileManager.default.url(for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: app, create: true)
        defer { try? FileManager.default.removeItem(at: work) }
        let ditto = Process()
        ditto.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        ditto.arguments = ["-x", "-k", zip.path, work.path]
        try ditto.run()
        ditto.waitUntilExit()
        let new = work.appendingPathComponent("ClipBubble.app")
        guard ditto.terminationStatus == 0, FileManager.default.fileExists(atPath: new.path) else { throw UpdateError("the download couldn't be unpacked") }
        try verify(new, version: r.version, against: app)

        progress("Installing \(r.version)…")
        _ = try FileManager.default.replaceItemAt(app, withItemAt: new, backupItemName: nil, options: .usingNewMetadataOnly)
    }

    /// The new copy must be ClipBubble at the advertised version, intact, and when this copy is signed by a team,
    /// signed by the same one.
    private nonisolated static func verify(_ new: URL, version: String, against app: URL) throws {
        let info = NSDictionary(contentsOf: new.appendingPathComponent("Contents/Info.plist"))
        guard info?["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              info?["CFBundleShortVersionString"] as? String == version
        else { throw UpdateError("the download isn't ClipBubble \(version)") }

        var newCode: SecStaticCode?, oldCode: SecStaticCode?, requirement: SecRequirement?
        guard SecStaticCodeCreateWithPath(new as CFURL, [], &newCode) == errSecSuccess, let newCode,
              SecStaticCodeCreateWithPath(app as CFURL, [], &oldCode) == errSecSuccess, let oldCode
        else { throw UpdateError("the download's signature couldn't be read") }
        if teamID(oldCode) != nil {
            guard SecCodeCopyDesignatedRequirement(oldCode, [], &requirement) == errSecSuccess else {
                throw UpdateError("this copy's signature couldn't be read")
            }
        }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
        guard SecStaticCodeCheckValidity(newCode, flags, requirement) == errSecSuccess else {
            throw UpdateError("the download isn't signed the same way as this copy, so it wasn't installed")
        }
    }

    private nonisolated static func teamID(_ code: SecStaticCode) -> String? {
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess else { return nil }
        return (info as? [String: Any])?[kSecCodeInfoTeamIdentifier as String] as? String
    }

    /// Waits for this copy to quit, then opens the new one.
    private static func reopen(_ app: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", #"while kill -0 "$1" 2>/dev/null; do sleep 0.2; done; open "$2""#, "sh", String(getpid()), app.path]
        try? p.run()
        NSApp.terminate(nil)
    }
}

struct UpdateError: LocalizedError {
    let errorDescription: String?
    init(_ s: String) { errorDescription = s }
}

/// Settings → General: the version, whether there's a newer one, and the button that installs it.
struct UpdateRow: View {
    @ObservedObject var updater: Updater

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 10) {
                Image(systemName: "arrow.down.circle").font(.system(size: 13)).foregroundStyle(.secondary).frame(width: 20)
                VStack(alignment: .leading, spacing: 2) {
                    Text("ClipBubble \(Updater.current)").font(.system(size: 13, weight: .medium))
                    detail
                }
                Spacer(minLength: 8)
                action
            }
            Toggle("Check for updates automatically", isOn: $updater.automatic)
                .toggleStyle(.checkbox).font(.system(size: 11)).foregroundStyle(.secondary).padding(.leading, 30)
        }
        .padding(.vertical, 5)
    }

    @ViewBuilder private var detail: some View {
        switch updater.state {
        case .idle: note("Checks GitHub for a new version every few hours.")
        case .checking: note("Checking…")
        case .current: note("Up to date.")
        case .available(let r):
            HStack(spacing: 6) {
                note("Version \(r.version) is available.")
                Link("What's new", destination: r.page).font(.system(size: 11))
            }
            if !Updater.signedByTeam {
                note("macOS will ask for ClipBubble's permissions again after updating.")
            }
        case .installing(let s): note(s)
        case .failed(let s):
            Text(s).font(.system(size: 11)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            Link("Open the releases page", destination: Updater.releases).font(.system(size: 11))
        }
    }

    @ViewBuilder private var action: some View {
        switch updater.state {
        case .available(let r): Button("Update") { updater.install(r) }.controlSize(.small)
        case .checking, .installing: ProgressView().controlSize(.small)
        case .failed: Button("Try again") { updater.check() }.controlSize(.small)
        case .idle, .current: Button("Check now") { updater.check() }.controlSize(.small)
        }
    }

    private func note(_ s: String) -> some View {
        Text(s).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }
}
