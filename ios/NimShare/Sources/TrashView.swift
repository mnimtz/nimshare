import SwiftUI

struct TrashView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [TrashItemDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var busy = false

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView(String(localized: "Papierkorb leer"), systemImage: "trash",
                    description: Text("Gelöschte Dateien landen hier und können wiederhergestellt werden."))
            } else {
                List {
                    ForEach(items) { item in
                        HStack {
                            Image(systemName: "doc")
                                .foregroundStyle(Theme.warnRed)
                                .frame(width: 24)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.name).lineLimit(2)
                                HStack(spacing: 8) {
                                    if let d = item.deletedAt {
                                        Text(d.formatted(.relative(presentation: .named)))
                                            .font(.caption).foregroundStyle(.secondary)
                                    }
                                    Text(ByteCountFormatter.string(fromByteCount: item.sizeBytes, countStyle: .file))
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                        // Restore is declared first so it sits closest to the
                        // swiped edge — the primary/safer action. Purge stays
                        // behind it and requires a longer swipe.
                        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                            Button { Task { await restore(item.id) } } label: {
                                Label("Wiederherstellen", systemImage: "arrow.uturn.backward")
                            }
                            .tint(Theme.tungstenBlue)
                            Button(role: .destructive) { Task { await purge(item.id) } } label: {
                                Label("Endgültig", systemImage: "xmark.bin")
                            }
                        }
                    }
                }
            }
            if let e = error {
                Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding()
            }
        }
        .navigationTitle(String(localized: "Papierkorb"))
        .task { await load() }
        .refreshable { await load() }
        .overlay { if busy { ProgressView() } }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.listTrash() }
        catch let ex { error = ex.localizedDescription }
    }

    private func restore(_ id: UUID) async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do { try await api.restoreFromTrash(id); await load() }
        catch let ex { error = ex.localizedDescription }
    }

    private func purge(_ id: UUID) async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do { try await api.purgeFromTrash(id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
}
