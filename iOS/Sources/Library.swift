import SwiftUI

/// What an item is, as the iPhone shows and filters it.
enum Look: String, CaseIterable, Identifiable {
    case text = "Text", link = "Links", image = "Images", file = "Files"

    var id: Self { self }

    var name: String {
        switch self {
        case .text: return "Text"
        case .link: return "Link"
        case .image: return "Image"
        case .file: return "File"
        }
    }

    var symbol: String {
        switch self {
        case .text: return "text.alignleft"
        case .link: return "link"
        case .image: return "photo"
        case .file: return "doc"
        }
    }

    var tint: Color {
        switch self {
        case .text: return .gray
        case .link: return .blue
        case .image: return .purple
        case .file: return .orange
        }
    }
}

extension Clip {
    var look: Look {
        switch kind {
        case "text": return isLink ? .link : .text
        case "image": return .image
        default: return imagePath != nil ? .image : .file
        }
    }

    /// One web address and nothing else.
    private var isLink: Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.contains(where: \.isWhitespace), let scheme = URL(string: t)?.scheme?.lowercased() else { return false }
        return scheme == "http" || scheme == "https"
    }
}

/// Everything ClipBubble keeps, as a grid by day, with search and filters by type and by device.
struct LibraryView: View {
    @EnvironmentObject var model: Model
    @State private var query = ""
    @State private var look: Look?
    /// The device an item came from; "" is this iPhone, nil is everywhere.
    @State private var from: String?
    @State private var all: [Clip] = []
    @Namespace private var chips
    @State private var viewing: Clip?

    private var shown: [Clip] { all.filter { (look == nil || $0.look == look) && (from == nil || $0.origin == from) } }

    private var days: [(day: Date, clips: [Clip])] {
        Dictionary(grouping: shown) { Calendar.current.startOfDay(for: $0.created) }
            .sorted { $0.key > $1.key }.map { ($0.key, $0.value) }
    }

    var body: some View {
        ScrollView {
            filters
            if shown.isEmpty {
                ContentUnavailableView(query.isEmpty ? "Nothing here" : "No matches", systemImage: look?.symbol ?? "square.grid.2x2",
                                       description: Text(query.isEmpty ? "Try another filter." : "Try other words or another filter."))
                    .padding(.top, 60)
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 160), spacing: 10)], spacing: 10, pinnedViews: [.sectionHeaders]) {
                ForEach(days, id: \.day) { d in
                    Section {
                        ForEach(d.clips, id: \.hash) { c in
                            Button { model.copy(c) } label: {
                                LibraryTile(clip: c, device: c.origin.isEmpty ? nil : model.devices[c.origin])
                            }
                            .buttonStyle(Pressable())
                            .clipMenu(c) { viewing = c }
                        }
                    } header: {
                        Text(title(d.day)).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 6)
                            .background(Color(.systemGroupedBackground))
                    }
                }
            }
            .padding(.horizontal)
            .padding(.bottom, 20)
            .animation(.snappy, value: look)
            .animation(.snappy, value: from)
        }
        .background(Color(.systemGroupedBackground))
        .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Search everything")
        .navigationTitle("Library")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: query) { all = model.library(query: query) }
        .onChange(of: model.clips) { withAnimation(.snappy) { all = model.library(query: query) } }
        .navigationDestination(item: $viewing) { ClipDetail(clip: $0) }
        .toast(model.toast)
    }

    /// Devices with something in the library. Names arrive for every device a computer knows, used or not.
    private var sources: [(key: String, value: String)] {
        let used = Set(all.map(\.origin))
        return model.devices.filter { used.contains($0.key) }.sorted { $0.value < $1.value }
    }

    private var filters: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                chip("All", symbol: nil, count: all.count, on: look == nil) { withAnimation(.snappy) { look = nil } }
                ForEach(Look.allCases) { l in
                    chip(l.rawValue, symbol: l.symbol, count: all.filter { $0.look == l }.count, on: look == l) { withAnimation(.snappy) { look = look == l ? nil : l } }
                }
                Menu {
                    Picker("From", selection: $from) {
                        Text("Everywhere").tag(String?.none)
                        Text("This iPhone").tag(String?.some(""))
                        ForEach(sources, id: \.key) { Text($0.value).tag(String?.some($0.key)) }
                    }
                } label: {
                    Label(from.map { $0.isEmpty ? "This iPhone" : model.devices[$0] ?? "Device" } ?? "From", systemImage: "laptopcomputer.and.iphone")
                        .font(.subheadline.weight(.medium))
                        .padding(.horizontal, 12).padding(.vertical, 7)
                        .background(from == nil ? Color(.secondarySystemGroupedBackground) : Color.accentColor, in: Capsule())
                        .foregroundStyle(from == nil ? Color.primary : .white)
                }
            }
            .padding(.horizontal)
            .padding(.vertical, 8)
        }
    }

    private func chip(_ title: String, symbol: String?, count: Int, on: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 5) {
                if let symbol { Image(systemName: symbol) }
                Text(title)
                Text("\(count)").foregroundStyle(on ? .white.opacity(0.75) : .secondary)
            }
            .font(.subheadline.weight(.medium))
            .padding(.horizontal, 12).padding(.vertical, 7)
            .background {
                // One highlight that slides to the chosen chip.
                if on { Capsule().fill(Color.accentColor).matchedGeometryEffect(id: "chosen", in: chips) }
                else { Capsule().fill(Color(.secondarySystemGroupedBackground)) }
            }
            .foregroundStyle(on ? .white : .primary)
        }
        .buttonStyle(Pressable())
    }

    private func title(_ day: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(day) { return "Today" }
        if cal.isDateInYesterday(day) { return "Yesterday" }
        return day.formatted(.dateTime.weekday(.wide).day().month())
    }
}

struct LibraryTile: View {
    let clip: Clip
    let device: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Color(.secondarySystemGroupedBackground).frame(height: 130)
                .overlay(alignment: .topLeading) { content }
                .clipShape(RoundedRectangle(cornerRadius: 12))
            HStack(spacing: 4) {
                Image(systemName: clip.remote ? "icloud.and.arrow.down" : clip.look.symbol).foregroundStyle(clip.remote ? .teal : clip.look.tint)
                Text(device ?? clip.source).lineLimit(1)
                Spacer(minLength: 4)
                Text(clip.created, format: .dateTime.hour().minute())
            }
            .font(.caption2).foregroundStyle(.secondary)
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(clip.spoken)
        .accessibilityValue([device ?? clip.source, clip.created.formatted(date: .omitted, time: .shortened)].filter { !$0.isEmpty }.joined(separator: ", "))
    }

    @ViewBuilder private var content: some View {
        if let p = clip.imagePath, !clip.remote {
            Thumb(p, pixels: 480).scaledToFill().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if clip.look == .file || clip.remote {
            VStack(spacing: 8) {
                Image(systemName: clip.remote ? "icloud.and.arrow.down" : "doc.fill").font(.largeTitle).foregroundStyle(clip.remote ? .teal : .orange)
                Text(clip.names)
                    .font(.caption).lineLimit(2).multilineTextAlignment(.center)
            }
            .padding(10).frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            Text(clip.text.trimmingCharacters(in: .whitespacesAndNewlines))
                .font(.callout).lineLimit(6)
                .foregroundStyle(clip.look == .link ? Color.blue : .primary)
                .padding(10)
        }
    }
}
