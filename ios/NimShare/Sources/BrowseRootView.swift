import SwiftUI

struct BrowseRootView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var scopes: [ScopeTile] = []
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        Group {
            if loading {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error {
                errorView(e)
            } else if scopes.isEmpty {
                ContentUnavailableView("Keine Bibliotheken", systemImage: "folder", description: Text("Der Server hat keine Bibliotheken zurückgegeben."))
            } else {
                content
            }
        }
        .navigationTitle("Dateien")
        .task { await load() }
        .refreshable { await load() }
    }

    private var content: some View {
        List {
            Section("Bibliotheken") {
                ForEach(scopes.filter { $0.scope.lowercased() == "personal" }) { tile in
                    scopeRow(tile)
                }
                ForEach(scopes.filter { $0.scope.lowercased() == "public" }) { tile in
                    scopeRow(tile)
                }
                let groups = scopes.filter { $0.scope.lowercased() == "group" }
                ForEach(groups) { tile in
                    scopeRow(tile)
                }
            }

            Section("Übersichten") {
                NavigationLink { FavoritesView() } label: {
                    Label("Favoriten", systemImage: "star.fill").foregroundStyle(.yellow)
                }
                NavigationLink { SharedWithMeView() } label: {
                    Label("Für mich freigegeben", systemImage: "person.crop.circle.badge.checkmark")
                        .foregroundStyle(Theme.tungstenBlue)
                }
                NavigationLink { LinksView() } label: {
                    Label("Meine Links", systemImage: "link").foregroundStyle(Theme.tungstenBlue)
                }
                NavigationLink { SignaturesView() } label: {
                    Label("Signaturen", systemImage: "signature").foregroundStyle(Theme.tungstenBlue)
                }
                NavigationLink { ActivityView() } label: {
                    Label("Aktivität", systemImage: "clock.fill").foregroundStyle(Theme.tungstenBlue)
                }
                NavigationLink { TrashView() } label: {
                    Label("Papierkorb", systemImage: "trash").foregroundStyle(Theme.warnRed)
                }
            }
        }
    }

    private func scopeRow(_ tile: ScopeTile) -> some View {
        // v1.10.71: Scope-Namen lokalisieren. Server liefert englisch
        // ("Personal"/"Public"/"Group"), iOS zeigt es auf Deutsch.
        let localized: String = {
            switch tile.scope.lowercased() {
            case "personal": return "Persönlich"
            case "public": return "Öffentlich"
            case "group": return "Gruppe"
            default: return tile.scope.capitalized
            }
        }()
        return NavigationLink {
            FolderBrowserView(scope: tile.scope, groupId: tile.groupId, path: "", title: localized)
        } label: {
            HStack(spacing: 14) {
                Image(systemName: tile.systemImage)
                    .font(.title2)
                    .foregroundStyle(Theme.tungstenBlue)
                    .frame(width: 32)
                VStack(alignment: .leading, spacing: 2) {
                    Text(localized).font(.body.weight(.medium))
                    if tile.scope.lowercased() == "group" {
                        // Bei Gruppen ist tile.name der Gruppen-Name — als Sub-Zeile.
                        Text(tile.name).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .padding(.vertical, 4)
        }
    }

    private func errorView(_ e: String) -> some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
            Text(e).multilineTextAlignment(.center).padding(.horizontal)
            Button("Erneut versuchen") { Task { await load() } }
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { scopes = try await api.scopes() }
        catch let e as ApiError {
            error = e.localizedDescription
            if case .notAuthorized = e { auth.signOut() }
        }
        catch let ex { error = ex.localizedDescription }
    }
}
