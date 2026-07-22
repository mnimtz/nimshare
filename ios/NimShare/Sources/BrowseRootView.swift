import SwiftUI

struct BrowseRootView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var scopes: [ScopeTile] = []
    @State private var loading = true
    @State private var error: String?
    // v1.10.123: gemessene Höhe der Begrüssung, damit das Kachel-Raster den
    // exakt verbleibenden Platz füllt — egal ob die Begrüssung 1 oder 4 Zeilen
    // lang ist. Siehe adaptiveHome.
    @State private var greetingHeight: CGFloat = 0

    var body: some View {
        Group {
            if loading && scopes.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, scopes.isEmpty {
                errorView(e)
            } else if scopes.isEmpty {
                ContentUnavailableView("Keine Bibliotheken", systemImage: "folder", description: Text("Der Server hat keine Bibliotheken zurückgegeben."))
            } else {
                adaptiveHome
            }
        }
        // v1.10.128: Grosser „Dateien"-Titel entfernt — er nahm nur Platz weg.
        // Die formatierte Begrüssung (Anrede + Nachricht) dient jetzt als
        // Header. Nav-Bar bleibt inline (leer) erhalten, damit das Wetter-
        // Symbol oben rechts weiter Platz hat.
        .navigationTitle("")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            // v1.10.122: Wetter-Symbol oben rechts (heutige Vorhersage, GPS).
            ToolbarItem(placement: .topBarTrailing) {
                WeatherToolbarItem()
            }
        }
        .task { await load(showSpinner: true) }
        .refreshable { await load(showSpinner: false) }
    }

    // MARK: - Adaptives Ein-Screen-Layout

    /// v1.10.123: Statt scrollender List ein Raster, das sich der Fläche
    /// anpasst. Die Begrüssung nimmt oben ihren natürlichen Platz; der Rest der
    /// Höhe wird auf die Kacheln verteilt, deren Größe (und damit Icon-/Text-
    /// Größe) dynamisch berechnet wird. So ist immer alles auf EINEM Screen —
    /// kurze Begrüssung → große Kacheln, lange Begrüssung → kompaktere Kacheln.
    /// GeometryReader macht es zugleich responsiv über alle iPhone-Größen und
    /// iPad (dort 3 Spalten statt 2). ScrollView bleibt als Sicherheitsnetz für
    /// sehr kleine Geräte / sehr lange Begrüssungen erhalten.
    private var adaptiveHome: some View {
        GeometryReader { geo in
            let libs = librarySpecs
            let overviews = overviewSpecs
            let cols = geo.size.width > 700 ? 3 : 2
            let libRows = max(1, Int(ceil(Double(libs.count) / Double(cols))))
            let ovRows = max(1, Int(ceil(Double(overviews.count) / Double(cols))))
            let totalRows = libRows + ovRows
            let outerPad: CGFloat = 16
            let gap: CGFloat = 12
            let dividerH: CGFloat = 22   // Höhe des Trenn-Blocks
            // Verfügbare Höhe fürs Raster = Screen − Begrüssung − Ränder −
            // Lücken (Begrüssung→Lib, Lib→Divider, Divider→Übersicht) − Divider.
            let avail = geo.size.height - greetingHeight - outerPad * 2 - gap * 3 - dividerH
            let tileH = max(78, (avail - CGFloat(totalRows - 1) * gap) / CGFloat(totalRows))
            let columns = Array(repeating: GridItem(.flexible(), spacing: gap), count: cols)

            ScrollView {
                VStack(spacing: gap) {
                    GreetingBanner()
                        .background(GeometryReader { g in
                            Color.clear.preference(key: GreetHeightKey.self, value: g.size.height)
                        })
                    // Bibliotheken — die eigentlichen Ablageorte.
                    LazyVGrid(columns: columns, spacing: gap) {
                        ForEach(libs) { s in
                            NavigationLink { s.dest() } label: { tileCard(s, height: tileH) }
                                .buttonStyle(.plain)
                        }
                    }
                    // v1.10.131: optische Trennung — macht klar, dass die
                    // ersten beiden Kacheln die Ablage sind und darunter die
                    // Übersichten kommen.
                    sectionDivider
                    // Übersichten & Werkzeuge.
                    LazyVGrid(columns: columns, spacing: gap) {
                        ForEach(overviews) { s in
                            NavigationLink { s.dest() } label: { tileCard(s, height: tileH) }
                                .buttonStyle(.plain)
                        }
                    }
                }
                .padding(outerPad)
                .frame(minHeight: geo.size.height)   // mindestens ein Screen füllen
            }
            .onPreferenceChange(GreetHeightKey.self) { greetingHeight = $0 }
        }
    }

    /// v1.10.131: dezente, beschriftete Trennlinie zwischen Bibliotheken und
    /// Übersichten.
    private var sectionDivider: some View {
        HStack(spacing: 10) {
            Rectangle().fill(Color.secondary.opacity(0.25)).frame(height: 1)
            Text("Übersichten")
                .font(.caption2.weight(.medium))
                .foregroundStyle(.secondary)
                .fixedSize()
            Rectangle().fill(Color.secondary.opacity(0.25)).frame(height: 1)
        }
        .padding(.vertical, 2)
    }

    /// Eine Kachel: Icon oben, Titel darunter. Alle Größen skalieren mit der
    /// berechneten Kachelhöhe, damit das Layout auf jedem Gerät stimmig bleibt.
    private func tileCard(_ s: TileSpec, height: CGFloat) -> some View {
        VStack(spacing: max(4, height * 0.07)) {
            Image(systemName: s.icon)
                .font(.system(size: min(max(height * 0.30, 20), 46)))
                .foregroundStyle(s.tint)
            Text(s.title)
                .font(.system(size: min(max(height * 0.13, 11), 16), weight: .semibold))
                .foregroundStyle(.primary)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            if let sub = s.subtitle {
                Text(sub)
                    .font(.system(size: min(max(height * 0.10, 9), 12)))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            }
        }
        .padding(.horizontal, 8)
        .frame(maxWidth: .infinity)
        .frame(height: height)
        .background(
            RoundedRectangle(cornerRadius: 16)
                .fill(Theme.tungstenBlue.opacity(0.06))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 16)
                .stroke(Theme.tungstenBlue.opacity(0.12), lineWidth: 1)
        )
        .contentShape(RoundedRectangle(cornerRadius: 16))
    }

    // MARK: - Kachel-Definitionen

    /// Ein Kachel-Deskriptor. `dest` ist ein Closure, damit die Ziel-View erst
    /// beim Antippen (nicht schon beim Rendern des Rasters) gebaut wird.
    private struct TileSpec: Identifiable {
        let id: String
        let title: String
        let subtitle: String?
        let icon: String
        let tint: Color
        let dest: () -> AnyView
    }

    /// Bibliotheken (Persönlich, dann Öffentlich) — die eigentlichen Ablage-
    /// orte für Dateien/Ordner. Gruppen bewusst nicht als Kachel (v1.10.103).
    private var librarySpecs: [TileSpec] {
        var t: [TileSpec] = []
        for tile in scopes.filter({ $0.scope.lowercased() == "personal" })
                        + scopes.filter({ $0.scope.lowercased() == "public" }) {
            let localized: String = tile.scope.lowercased() == "personal" ? "Persönlich"
                : tile.scope.lowercased() == "public" ? "Öffentlich" : tile.scope.capitalized
            t.append(TileSpec(id: "lib-\(tile.id)", title: localized, subtitle: nil,
                              icon: tile.systemImage, tint: Theme.tungstenBlue,
                              dest: { AnyView(FolderBrowserView(scope: tile.scope, groupId: tile.groupId, path: "", title: localized)) }))
        }
        return t
    }

    /// Übersichten & Werkzeuge — unter der Trennlinie.
    private var overviewSpecs: [TileSpec] {
        var t: [TileSpec] = []
        t.append(TileSpec(id: "fav", title: "Favoriten", subtitle: nil, icon: "star.fill", tint: .yellow, dest: { AnyView(FavoritesView()) }))
        t.append(TileSpec(id: "shared", title: "Freigegeben", subtitle: "für mich", icon: "person.crop.circle.badge.checkmark", tint: Theme.tungstenBlue, dest: { AnyView(SharedWithMeView()) }))
        t.append(TileSpec(id: "links", title: "Meine Links", subtitle: nil, icon: "link", tint: Theme.tungstenBlue, dest: { AnyView(LinksView()) }))
        t.append(TileSpec(id: "sign", title: "Signaturen", subtitle: nil, icon: "signature", tint: Theme.tungstenBlue, dest: { AnyView(SignaturesView()) }))
        t.append(TileSpec(id: "activity", title: "Aktivität", subtitle: nil, icon: "clock.fill", tint: Theme.tungstenBlue, dest: { AnyView(ActivityView()) }))
        // v1.10.126: Papierkorb ist ins Profil gewandert, hier steht dafür die
        // v1.10.133: „Bookmarks" (vorher „Linksammlung" — kollidierte mit
        // „Meine Links"). Fixer Begriff in allen Sprachen.
        t.append(TileSpec(id: "linkcol", title: "Bookmarks", subtitle: nil, icon: "bookmark.fill", tint: Theme.tungstenBlue, dest: { AnyView(LinkCollectionView()) }))
        return t
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

/// v1.10.123: Misst die Höhe der Begrüssung, damit das Raster den Rest füllt.
private struct GreetHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}
