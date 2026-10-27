import SwiftUI
import UIKit

/// Share → ClipBubble: anything shareable goes into the history and onto your computers' clipboard.
@objc(ShareViewController)
final class ShareViewController: UIViewController {
    private let state = ShareState()

    override func viewDidLoad() {
        super.viewDidLoad()
        let host = UIHostingController(rootView: ShareCard(state: state) { [weak self] in self?.close() })
        host.view.backgroundColor = .clear
        addChild(host)
        view.addSubview(host.view)
        host.view.frame = view.bounds
        host.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        host.didMove(toParent: self)

        let providers = (extensionContext?.inputItems as? [NSExtensionItem] ?? []).flatMap { $0.attachments ?? [] }
        Task { @MainActor in
            let message = await Courier.deliver(status: { [state] in state.text = $0 }) { [state] in
                let hashes = await Inbox.add(providers, source: "Share", picturesAsFiles: true)
                state.saved = !hashes.isEmpty
                return hashes
            }
            state.text = message
            state.done = true
            try? await Task.sleep(nanoseconds: 1_400_000_000)
            close()
        }
    }

    private func close() {
        guard !state.closed else { return }
        state.closed = true
        extensionContext?.completeRequest(returningItems: nil)
    }
}

final class ShareState: ObservableObject {
    @Published var text = "Connecting…"
    @Published var done = false
    /// Hiding before this would close the share sheet with nothing saved.
    @Published var saved = false
    var closed = false
}

struct ShareCard: View {
    @ObservedObject var state: ShareState
    let close: () -> Void

    var body: some View {
        VStack {
            Spacer()
            HStack(spacing: 12) {
                if state.done {
                    Image(systemName: state.saved ? "checkmark.circle.fill" : "exclamationmark.circle.fill").font(.title2)
                        .foregroundStyle(state.saved ? .green : .orange).accessibilityHidden(true)
                } else {
                    ProgressView()
                }
                Text(state.text).font(.body.weight(.medium)).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                Button(state.done ? "Done" : "Hide", action: close).font(.subheadline).disabled(!state.saved && !state.done)
            }
            .padding(18)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 22))
            .padding(16)
        }
    }
}
