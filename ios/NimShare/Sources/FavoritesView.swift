import SwiftUI

struct FavoritesView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [FavoriteDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var previewFile: FileItem?

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView(String(localized: "Keine Favoriten"), systemImage: "star",
                    description: Text("Markiere Dateien mit ⭐, um sie hier zu sehen."))
            } else {
                List {
                    ForEach(items) { fav in
                        // v1.10.149: Ordner-Favoriten öffnen jetzt SharedFolderView
                        // (nutzt den scope-agnostischen /api/v1/folders/{id}/browse-
                        // Endpoint). Vorher zeigte der Tap nur einen „such es dir
                        // selbst"-Alert — der Kernwert von „Favorit" für Ordner
                        // war damit weg.
                        if fav.kind == "folder" {
                            NavigationLink {
                                SharedFolderView(folderId: fav.targetId, initialTitle: fav.name)
                            } label: { favRow(fav) }
                            .swipeActions {
                                Button(role: .destructive) { Task { await unstar(fav) } } label: {
                                    Label(String(localized: "Entfernen"), systemImage: "star.slash")
                                }
                            }
                        } else {
                            Button {
                                previewFile = FileItem(
                                    id: fav.targetId, name: fav.name, sizeBytes: 0,
                                    contentType: "application/octet-stream",
                                    createdAt: fav.createdAt, ownerName: nil,
                                    aiTags: nil, aiRiskFlag: nil)
                            } label: { favRow(fav) }
                            .buttonStyle(.plain)
                            .swipeActions {
                                Button(role: .destructive) { Task { await unstar(fav) } } label: {
                                    Label(String(localized: "Entfernen"), systemImage: "star.slash")
                                }
                            }
                        }
                    }
                }
            }
            if let e = error { Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding() }
        }
        .navigationTitle(String(localized: "Favoriten"))
        .task { await load() }
        .refreshable { await load() }
        .sheet(item: $previewFile) { f in NavigationStack { FilePreviewView(file: f) } }
    }

    @ViewBuilder
    private func favRow(_ fav: FavoriteDto) -> some View {
        HStack {
            Image(systemName: fav.kind == "file" ? "doc.fill" : "folder.fill")
                .foregroundStyle(fav.kind == "file" ? Color.blue : Color.orange)
                .frame(width: 24)
            VStack(alignment: .leading, spacing: 2) {
                Text(fav.name).lineLimit(2)
                Text(fav.createdAt.formatted(date: .abbreviated, time: .shortened))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Image(systemName: "star.fill").foregroundStyle(.yellow)
        }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.listFavorites() }
        catch let ex { error = ex.localizedDescription }
    }

    private func unstar(_ fav: FavoriteDto) async {
        guard let api = auth.api else { return }
        do {
            _ = try await api.toggleFavorite(
                fileId: fav.kind == "file" ? fav.targetId : nil,
                folderId: fav.kind == "folder" ? fav.targetId : nil)
            await load()
        } catch let ex { error = ex.localizedDescription }
    }
}
