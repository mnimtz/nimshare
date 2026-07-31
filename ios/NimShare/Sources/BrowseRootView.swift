import SwiftUI

/// v1.11.67 (redesign-Pilot, Branch redesign/ios-mobile-v2): Home komplett
/// neu nach dem nimshare-handoff-Design-Prototyp (Turn 1 · Option 1a,
/// "playful/soft"). Ersetzt das bisherige Einzelbildschirm-Raster (fixe
/// Kachelhöhe per GeometryReader) durch ein normales scrollendes Layout:
/// Begrüssung → "Zuletzt geteilt"-Hero (horizontal scrollend, echte Link-
/// Statistiken) → Bibliotheken-Grid → Werkzeuge-Grid. Datenfluss (scopes,
/// TileSpec-Deskriptoren, Navigationsziele) unverändert — nur Layout/Stil.
struct BrowseRootView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var scopes: [ScopeTile] = []
    @State private var recentLinks: [ShareLinkDto] = []
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        ZStack {
            Theme.bgGradient.ignoresSafeArea()
            Group {
                if loading && scopes.isEmpty {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let e = error, scopes.isEmpty {
                    errorView(e)
                } else if scopes.isEmpty {
                    RSEmptyState(systemImage: "folder", title: "Keine Bibliotheken",
                                 desc: "Der Server hat keine Bibliotheken zurückgegeben.")
                } else {
                    homeContent
                }
            }
        }
        .navigationTitle("")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load(showSpinner: true) }
        .refreshable { await load(showSpinner: false) }
    }

    // MARK: - Home content

    private var homeContent: some View {
        ScrollView {
            VStack(spacing: 0) {
                GreetingBanner()
                if !recentLinks.isEmpty {
                    recentHero.padding(.top, Theme.Space.lg)
                }
                RSSectionHeader(title: "Bibliotheken")
                tileGrid(librarySpecs)
                RSSectionHeader(title: "Werkzeuge")
                tileGrid(overviewSpecs)
                Spacer(minLength: Theme.Space.xxl)
            }
        }
    }

    // MARK: - "Zuletzt geteilt" hero (horizontal scroll, echte Link-Stats)

    @ViewBuilder
    private var recentHero: some View {
        let weekAgo = Calendar.current.date(byAdding: .day, value: -7, to: Date()) ?? .distantPast
        let thisWeek = recentLinks.filter { $0.createdAt >= weekAgo }.count
        let totalHits = recentLinks.reduce(0) { $0 + $1.hitCount }
        NavigationLink {
            LinksView()
        } label: {
            VStack(alignment: .leading, spacing: 0) {
                Text("ZULETZT GETEILT")
                    .font(TFont.micro)
                    .kerning(0.9)
                    .foregroundStyle(.white.opacity(0.7))
                    .padding(.bottom, 2)
                Text("\(thisWeek) Links diese Woche · \(totalHits) Aufrufe")
                    .font(TFont.titleM)
                    .foregroundStyle(.white)
                    .padding(.bottom, Theme.Space.md)

                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 8) {
                        ForEach(recentLinks.prefix(8)) { link in
                            recentCard(link)
                        }
                    }
                }
            }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.heroGradient)
            .clipShape(RoundedRectangle(cornerRadius: Theme.Radius2.hero, style: .continuous))
            .padding(.horizontal, Theme.Space.lg)
            .shadow(color: Theme.navy.opacity(0.4), radius: 22, x: 0, y: 14)
        }
        .buttonStyle(.plain)
    }

    private func recentCard(_ link: ShareLinkDto) -> some View {
        let name = link.targetName ?? link.slug
        let info = FileFormatInfo.of(name)
        let display = (name as NSString).deletingPathExtension
        return VStack(alignment: .leading, spacing: 8) {
            Text(info.label)
                .font(.system(size: 10, weight: .heavy))
                .foregroundStyle(.white)
                .padding(.horizontal, 6).padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 10).fill(info.color))
            Text(display.isEmpty ? link.slug : display)
                .font(TFont.bodyS.weight(.semibold))
                .foregroundStyle(.white)
                .lineLimit(1)
            Text("\(link.hitCount) Aufrufe")
                .font(TFont.caption)
                .foregroundStyle(.white.opacity(0.65))
        }
        .padding(.horizontal, 11).padding(.vertical, 10)
        .frame(width: 120, alignment: .leading)
        .background(.ultraThinMaterial)
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(.white.opacity(0.18), lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 16))
    }

    // MARK: - Tile grid (3 Spalten, feste Karten-Optik)

    private func tileGrid(_ specs: [TileSpec]) -> some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: Theme.Space.md), count: 3),
                   spacing: Theme.Space.md) {
            ForEach(specs) { s in
                NavigationLink { s.dest() } label: { tileCard(s) }
                    .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, Theme.Space.xl)
        .padding(.bottom, Theme.Space.s)
    }

    private func tileCard(_ s: TileSpec) -> some View {
        VStack(alignment: .leading, spacing: Theme.Space.s) {
            Image(systemName: s.icon)
                .font(.system(size: 20, weight: .bold))
                .foregroundStyle(s.tint)
                .frame(width: 40, height: 40)
                .background(RoundedRectangle(cornerRadius: 12).fill(s.tint.opacity(0.14)))
            Text(s.title)
                .font(TFont.titleS)
                .foregroundStyle(Theme.textPrimary)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
            if let sub = s.subtitle {
                Text(sub)
                    .font(TFont.caption)
                    .foregroundStyle(Theme.textSecondary)
                    .lineLimit(1)
            }
        }
        .padding(Theme.Space.md)
        .frame(maxWidth: .infinity, minHeight: 92, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: Theme.Radius2.cardLarge, style: .continuous).fill(Theme.surface2))
        .overlay(RoundedRectangle(cornerRadius: Theme.Radius2.cardLarge, style: .continuous).stroke(Theme.border2, lineWidth: 1))
        .shadow(color: .black.opacity(0.05), radius: 8, x: 0, y: 6)
        .contentShape(RoundedRectangle(cornerRadius: Theme.Radius2.cardLarge))
    }

    // MARK: - Kachel-Definitionen (unverändert gegenüber v1.11.66)

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
                              icon: tile.systemImage, tint: Theme.navy,
                              dest: { AnyView(FolderBrowserView(scope: tile.scope, groupId: tile.groupId, path: "", title: localized)) }))
        }
        return t
    }

    /// Übersichten & Werkzeuge — unter der Trennlinie.
    private var overviewSpecs: [TileSpec] {
        var t: [TileSpec] = []
        t.append(TileSpec(id: "fav", title: "Favoriten", subtitle: nil, icon: "star.fill", tint: Theme.yellow, dest: { AnyView(FavoritesView()) }))
        // v1.11.42 — Marcus's Wunsch: Key-Store ("Lizenzverwaltung") war in
        // Profil versteckt, obwohl es ein Kernfeature ist — mit „Freigegeben"
        // getauscht (das zieht dafür nach Profil → Dateien um).
        t.append(TileSpec(id: "keystore", title: "Lizenzverwaltung", subtitle: nil, icon: "key.fill", tint: Theme.navy, dest: { AnyView(KeyStoreView()) }))
        t.append(TileSpec(id: "links", title: "Meine Links", subtitle: nil, icon: "link", tint: Theme.cyan, dest: { AnyView(LinksView()) }))
        t.append(TileSpec(id: "sign", title: "Signaturen", subtitle: nil, icon: "signature", tint: Theme.navy, dest: { AnyView(SignaturesView()) }))
        // v1.11.63: "Aktivität" ist ins Profil/Einstellungen gewandert (dort
        // unter "Dateien"), hier steht dafür "Benutzerverwaltung" — admin-only,
        // 1:1-Parität mit /settings/users im Web (bislang nur Web-Feature).
        if auth.isAdmin {
            t.append(TileSpec(id: "users", title: "Benutzerverwaltung", subtitle: nil, icon: "person.2.fill", tint: Theme.navy, dest: { AnyView(UsersListView()) }))
        }
        // v1.10.126: Papierkorb ist ins Profil gewandert, hier steht dafür die
        // v1.10.133: „Bookmarks" (vorher „Linksammlung" — kollidierte mit
        // „Meine Links"). Fixer Begriff in allen Sprachen.
        t.append(TileSpec(id: "linkcol", title: "Bookmarks", subtitle: nil, icon: "bookmark.fill", tint: Theme.navy, dest: { AnyView(LinkCollectionView()) }))
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
            async let s = api.scopes()
            // v1.11.67: Links für die "Zuletzt geteilt"-Hero — rein
            // dekorativ, darf beim Fehlschlagen den Home-Screen nicht
            // blockieren, daher `try?` statt throw.
            async let linksTask: [ShareLinkDto] = (try? await api.listMyLinks()) ?? []
            scopes = try await s
            let links = await linksTask
            recentLinks = links
                .filter { !$0.isRevoked && ($0.expiresAt.map { $0 > Date() } ?? true) }
                .sorted { $0.createdAt > $1.createdAt }
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
