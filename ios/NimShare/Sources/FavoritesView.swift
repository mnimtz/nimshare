import SwiftUI

struct FavoritesView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [FavoriteDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var previewFile: FileItem?
    @State private var folderTargetInfo: String?  // shown as an alert for now

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
                        Button {
                            if fav.kind == "file" {
                                previewFile = FileItem(
                                    id: fav.targetId, name: fav.name, sizeBytes: 0,
                                    contentType: "application/octet-stream",
                                    createdAt: fav.createdAt, ownerName: nil,
                                    aiTags: nil, aiRiskFlag: nil)
                            } else {
                                // Folder favorites: we don't know the scope/path
                                // just from the favorite row, so surface the id
                                // to the user rather than pretending the tap
                                // did nothing.
                                folderTargetInfo = fav.name
                            }
                        } label: {
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
                        .buttonStyle(.plain)
                        .swipeActions {
                            Button(role: .destructive) {
                                Task { await unstar(fav) }
                            } label: {
                                Label(String(localized: "Entfernen"), systemImage: "star.slash")
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
        .alert("Ordner öffnen", isPresented: Binding(
            get: { folderTargetInfo != nil },
            set: { if !$0 { folderTargetInfo = nil } })) {
            Button("OK") { folderTargetInfo = nil }
        } message: {
            Text("Ordner-Favoriten öffnest du am schnellsten über die Bibliothek. Suche nach „\(folderTargetInfo ?? "")“.")
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
