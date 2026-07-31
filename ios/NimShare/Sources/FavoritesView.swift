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
                RSEmptyState(systemImage: "star", title: String(localized: "Keine Favoriten"),
                             desc: String(localized: "Markiere Dateien mit ⭐, um sie hier zu sehen."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
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
                            .listRowBackground(Theme.surface2)
                            .listRowSeparator(.hidden)
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
                            .listRowBackground(Theme.surface2)
                            .listRowSeparator(.hidden)
                            .swipeActions {
                                Button(role: .destructive) { Task { await unstar(fav) } } label: {
                                    Label(String(localized: "Entfernen"), systemImage: "star.slash")
                                }
                            }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            }
            if let e = error { Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding() }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle(String(localized: "Favoriten"))
        .task { await load() }
        .refreshable { await load() }
        .sheet(item: $previewFile) { f in FileDetailView(file: f) }
    }

    @ViewBuilder
    private func favRow(_ fav: FavoriteDto) -> some View {
        HStack(spacing: 12) {
            if fav.kind == "file" {
                FileFormatBadge(name: fav.name, size: 34)
            } else {
                Image(systemName: "folder.fill")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(Theme.navy)
                    .frame(width: 36, height: 36)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Theme.navy.opacity(0.12)))
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(fav.name).font(TFont.titleS).foregroundStyle(Theme.textPrimary).lineLimit(2)
                Text(fav.createdAt.formatted(date: .abbreviated, time: .shortened))
                    .font(TFont.caption).foregroundStyle(Theme.textSecondary)
            }
            Spacer()
            Image(systemName: "star.fill").foregroundStyle(Theme.yellow)
        }
        .padding(.vertical, 4)
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.listFavorites() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }

    private func unstar(_ fav: FavoriteDto) async {
        guard let api = auth.api else { return }
        do {
            _ = try await api.toggleFavorite(
                fileId: fav.kind == "file" ? fav.targetId : nil,
                folderId: fav.kind == "folder" ? fav.targetId : nil)
            await load()
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
