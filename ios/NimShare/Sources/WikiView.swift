import SwiftUI

/// v1.10.88: Wiki-Read-Ansicht — iOS-Parität zum Web-Wiki. MVP: nur
/// lesend, kein Erstellen/Bearbeiten (Wiki ist typischerweise Team-Doku
/// die am Desktop besser gepflegt wird). Scope-Picker Personal/Public.
struct WikiView: View {
    @EnvironmentObject var auth: AuthStore

    enum Scope: String, CaseIterable, Identifiable {
        case personal = "Personal", pub = "Public"
        var id: String { rawValue }
        var label: String {
            switch self {
            case .personal: return "Persönlich"
            case .pub: return "Öffentlich"
            }
        }
    }

    @State private var scope: Scope = .personal
    @State private var pages: [NimShareAPI.WikiPageDto] = []
    @State private var loading = false
    @State private var error: String?

    var body: some View {
        List {
            Section {
                Picker("Bereich", selection: $scope) {
                    ForEach(Scope.allCases) { s in Text(s.label).tag(s) }
                }
                .pickerStyle(.segmented)
            }
            if pages.isEmpty && !loading {
                ContentUnavailableView(
                    "Keine Wiki-Seiten",
                    systemImage: "book",
                    description: Text("In diesem Bereich sind noch keine Seiten angelegt. Wiki-Seiten werden im Web unter Wiki verwaltet."))
            }
            ForEach(pages) { p in
                NavigationLink { WikiPageDetailView(pageId: p.id) } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(p.title).font(.body.weight(.semibold))
                        HStack(spacing: 4) {
                            Text(p.createdByName).font(.caption).foregroundStyle(.secondary)
                            if let d = p.updatedAt as Date? {
                                Text("· \(d, style: .date)").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
            if let e = error {
                Section { Text(e).foregroundStyle(Theme.warnRed).font(.footnote) }
            }
        }
        .navigationTitle("Wiki")
        .task { await load() }
        .onChange(of: scope) { _, _ in Task { await load() } }
        .refreshable { await load() }
        .overlay { if loading && pages.isEmpty { ProgressView() } }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do { pages = try await api.wikiPages(scope: scope.rawValue) }
        catch let ex { error = ex.localizedDescription; pages = [] }
    }
}

struct WikiPageDetailView: View {
    let pageId: UUID
    @EnvironmentObject var auth: AuthStore
    @State private var page: NimShareAPI.WikiPageDto?
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        ScrollView {
            if let p = page {
                VStack(alignment: .leading, spacing: 12) {
                    Text(p.title).font(.title2.weight(.bold))
                    HStack(spacing: 6) {
                        Image(systemName: "person.circle")
                        Text(p.createdByName)
                        Text("·").foregroundStyle(.secondary)
                        Text(p.updatedAt, style: .date)
                    }
                    .font(.caption).foregroundStyle(.secondary)
                    Divider()
                    // Naive Markdown-Rendering — SwiftUI kann das ab iOS 15.
                    if let md = p.contentMarkdown, !md.isEmpty {
                        if let attr = try? AttributedString(markdown: md,
                            options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)) {
                            Text(attr).font(.body)
                        } else {
                            Text(md).font(.body)
                        }
                    } else {
                        Text("(Diese Seite hat noch keinen Inhalt.)")
                            .italic().foregroundStyle(.secondary)
                    }
                }
                .padding()
            } else if let e = error {
                Text(e).foregroundStyle(Theme.warnRed).padding()
            } else {
                ProgressView().padding()
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do { page = try await api.wikiPage(pageId) }
        catch let ex { error = ex.localizedDescription }
    }
}
