import AppIntents
import UIKit

/// Shortcuts, Siri, Back Tap and the Action button: send to the computers without opening ClipBubble.
struct SendToComputers: AppIntent {
    static let title: LocalizedStringResource = "Send to Computers"
    static let description = IntentDescription("Puts text or a file on the clipboard of your linked computers. With neither, it sends what's on this device's clipboard.")

    @Parameter(title: "Text") var text: String?
    @Parameter(title: "File") var file: IntentFile?

    static var parameterSummary: some ParameterSummary {
        Summary("Send \(\.$text) to computers") { \.$file }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let (text, file) = (text, file)
        let message = await Courier.deliver {
            if let file {
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
                try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let url = dir.appendingPathComponent(file.filename)
                guard (try? file.data.write(to: url)) != nil else { return [] }
                return Inbox.file(url, source: "Shortcuts").map { [$0] } ?? []
            }
            if let text { return Inbox.text(text, source: "Shortcuts").map { [$0] } ?? [] }
            return await Inbox.add(UIPasteboard.general.itemProviders, source: "Clipboard")
        }
        return .result(dialog: "\(message)")
    }
}

/// The other way: whatever you copied last on a computer, onto this iPhone's clipboard.
struct CopyLatestFromComputers: AppIntent {
    static let title: LocalizedStringResource = "Copy Latest from Computers"
    static let description = IntentDescription("Links to your computers, puts the newest item in ClipBubble's history on this device's clipboard, and returns its text.")

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<String> & ProvidesDialog {
        let engine = SyncEngine.shared
        engine.start()
        let end = Date().addingTimeInterval(10)
        while Date() < end, !engine.statuses.values.contains(where: \.ok), !SyncEngine.enabledHosts.isEmpty {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        // Long enough for the computers' recent history to arrive after the link comes up.
        try? await Task.sleep(nanoseconds: 1_500_000_000)
        guard let c = DB().clips(limit: 1).first else { return .result(value: "", dialog: "ClipBubble's history is empty.") }
        Clipboard.write(c)
        let what = c.kind == "text" ? c.text : c.paths.map { ($0 as NSString).lastPathComponent }.joined(separator: ", ")
        return .result(value: c.kind == "text" ? c.text : what, dialog: "Copied \(what.isEmpty ? "an image" : String(what.prefix(80)))")
    }
}

struct ClipBubbleShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: SendToComputers(), phrases: ["Send clipboard with \(.applicationName)", "\(.applicationName) send clipboard"],
                    shortTitle: "Send Clipboard", systemImageName: "arrow.up.doc.on.clipboard")
        AppShortcut(intent: CopyLatestFromComputers(), phrases: ["Copy latest from \(.applicationName)", "\(.applicationName) get clipboard"],
                    shortTitle: "Copy Latest", systemImageName: "arrow.down.doc.on.clipboard")
    }
}
