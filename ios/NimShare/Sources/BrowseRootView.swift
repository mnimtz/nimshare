import SwiftUI

struct BrowseRootView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var scopes: [ScopeTile] = []
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        Group {
            if loading && scopes.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, scopes.isEmpty {
                errorView(e)
            } else if scopes.isEmpty {
                ContentUnavailableView("Keine Bibliotheken", systemImage: "folder", description: Text("Der Server hat keine Bibliotheken zurückgegeben."))
            } else {
                content
            }
        }
        .navigationTitle("Dateien")
        .task { await load(showSpinner: true) }
        .refreshable { await load(showSpinner: false) }
    }

    private var content: some View {
        List {
            // v1.10.114: KI-Begrüssung ganz oben.
            GreetingBanner()
            Section("Bibliotheken") {
                ForEach(scopes.filter { $0.scope.lowercased() == "personal" }) { tile in
                    scopeRow(tile)
                }
                ForEach(scopes.filter { $0.scope.lowercased() == "public" }) { tile in
                    scopeRow(tile)
                }
                // v1.10.103: Group-Kacheln bewusst NICHT mehr rendern.
                // Server ab v1.10.102 liefert sie nicht mehr; dieser Client-
                // Guard verhindert die Kachel auch, falls ein älterer Server
                // sie noch mitschickt. Gruppen sind reine Verteiler-Namen
                // für „Teilen mit → Gruppe", keine Bibliothek.
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

    private func load(showSpinner: Bool = true) async {
        guard let api = auth.api else { return }
        // v1.10.103: Spinner nur beim initialen Laden. Bei Pull-to-Refresh
        // NICHT `loading = true` setzen — das würde den View-Body auf
        // `ProgressView` austauschen, wodurch der `.refreshable`-Task
        // cancelled wird → „Abgebrochen"-Fehler. Alten Scope-State behalten
        // und Fehler nur bei komplett leerem State zeigen.
        if showSpinner { loading = true }
        defer { if showSpinner { loading = false } }
        do {
            let s = try await api.scopes()
            scopes = s
            error = nil
        }
        catch is CancellationError {
            // Refresh vom User oder System abgebrochen — kein Fehler zeigen.
        }
        catch let e as ApiError {
            if case .notAuthorized = e { auth.signOut(); return }
            if scopes.isEmpty { error = e.localizedDescription }
        }
        catch let ex {
            // URLError.cancelled → "Abgebrochen" — nur zeigen wenn wir
            // wirklich nichts anderes haben.
            let ns = ex as NSError
            let isCancel = ns.domain == NSURLErrorDomain && ns.code == NSURLErrorCancelled
            if !isCancel && scopes.isEmpty { error = ex.localizedDescription }
        }
    }
}
