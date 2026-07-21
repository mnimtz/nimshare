import SwiftUI

struct SearchView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var query = ""
    @State private var results: [SearchHitDto] = []
    @State private var busy = false
    @State private var error: String?
    @State private var hasSearched = false
    @State private var previewFileItem: FileItem?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Image(systemName: "sparkle.magnifyingglass").foregroundStyle(Theme.tungstenBlue)
                TextField("Dateien nach Bedeutung suchen…", text: $query)
                    .textFieldStyle(.plain)
                    .submitLabel(.search)
                    .onSubmit { Task { await run() } }
                if busy { ProgressView() }
                else if !query.isEmpty {
                    Button { query = ""; results = []; hasSearched = false } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary)
                    }
                }
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 10).fill(Theme.cardBackground))
            .padding()

            if let e = error {
                Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding(.horizontal)
            }

            if results.isEmpty {
                ContentUnavailableView(
                    hasSearched ? "Keine Treffer" : "Semantische Suche",
                    systemImage: hasSearched ? "magnifyingglass" : "sparkle.magnifyingglass",
                    description: Text(hasSearched
                        ? "Versuch andere Stichworte oder eine längere Formulierung."
                        : "Frag wie bei einer Suchmaschine — „Budget-Folien Q4" oder „Vertrag Lizenz". Benötigt einen konfigurierten AI-Provider in den Server-Einstellungen.")
                )
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(results) { hit in
                    Button { open(hit) } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(hit.name).lineLimit(2)
                                Spacer()
                                Text(Int(hit.score * 100).description + "%")
                                    .font(.caption.monospaced())
                                    .foregroundStyle(.secondary)
                            }
                            if let s = hit.snippet, !s.isEmpty {
                                Text(s).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                            }
                        }.padding(.vertical, 2)
                    }.buttonStyle(.plain)
                }
                .listStyle(.plain)
            }
        }
        .navigationTitle("Suche")
        .sheet(item: $previewFileItem) { f in
            NavigationStack { FilePreviewView(file: f) }
        }
    }

    private func run() async {
        guard let api = auth.api, !query.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        busy = true; error = nil; hasSearched = true
        defer { busy = false }
        do { results = try await api.semanticSearch(query: query) }
        catch let e as ApiError { error = e.localizedDescription }
        catch let ex { error = ex.localizedDescription }
    }

    private func open(_ hit: SearchHitDto) {
        previewFileItem = FileItem(
            id: hit.id, name: hit.name, sizeBytes: 0,
            contentType: "application/octet-stream",
            createdAt: Date(), ownerName: nil,
            aiTags: nil, aiRiskFlag: nil)
    }
}
