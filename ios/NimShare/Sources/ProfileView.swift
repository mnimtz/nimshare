import SwiftUI

struct ProfileView: View {
    @EnvironmentObject var auth: AuthStore
    // v1.11.63: "Sprache"-Zeile war bisher reines LabeledContent, das
    // User.PreferredCulture roh anzeigte — ein Feld, das die App nie
    // schrieb und das serverseitig beim Default "en" blieb. Jetzt ein
    // echter Picker, der das Feld setzt UND (nach Neustart) die App-UI-
    // Sprache selbst umstellt (AppleLanguages-Override).
    @State private var cultureBusy = false
    @State private var cultureError: String?
    @State private var showRestartHint = false
    private let cultureOptions = [("de", "Deutsch"), ("en", "English"), ("fr", "Français"),
                                   ("it", "Italiano"), ("es", "Español"), ("nl", "Nederlands")]

    // v1.11.73: Erscheinungsbild-Picker — siehe AppearanceMode in NimShareApp.swift.
    @AppStorage("appearance.mode") private var appearanceRaw: String = AppearanceMode.system.rawValue

    var body: some View {
        Form {
            Section {
                HStack(spacing: 16) {
                    AvatarView(user: auth.user, size: 72)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(auth.user?.displayName ?? "").font(TFont.titleM).foregroundStyle(Theme.textPrimary)
                        Text(auth.user?.email ?? "").font(TFont.bodyS).foregroundStyle(Theme.textSecondary)
                        if let role = auth.user?.role {
                            Chip(text: role, color: Theme.navy, bg: Theme.navy.opacity(0.12))
                        }
                    }
                }
                .padding(.vertical, 4)
            }
            .listRowBackground(Theme.surface2)

            if let u = auth.user {
                // v1.10.149: EN/DE-Mix behoben — vorher hartkodiertes
                // „Storage quota"/„Quota"/„Language" mitten in deutscher
                // Section-Umgebung.
                Section {
                    LabeledContent("Kontingent", value: ByteCountFormatter.string(fromByteCount: u.quotaBytes, countStyle: .file))
                    Picker("Sprache", selection: Binding(
                        get: { u.preferredCulture },
                        set: { newCode in Task { await setCulture(newCode) } }
                    )) {
                        ForEach(cultureOptions, id: \.0) { code, name in Text(name).tag(code) }
                    }
                    .disabled(cultureBusy)
                    if let e = cultureError { Text(e).font(TFont.caption).foregroundStyle(Theme.danger2) }
                } header: { RSSectionHeader(title: "Speicher") }
                    .listRowBackground(Theme.surface2)
            }

            // v1.11.73: Erscheinungsbild-Picker (Marcus's Wunsch).
            Section {
                Picker("Erscheinungsbild", selection: $appearanceRaw) {
                    ForEach(AppearanceMode.allCases) { mode in
                        Text(mode.label).tag(mode.rawValue)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .tint(Theme.navy)
            } header: { RSSectionHeader(title: "Erscheinungsbild") }
                .listRowBackground(Theme.surface2)

            // v1.10.126: Papierkorb von der Startseiten-Kachel hierher —
            // dafür ist „Linksammlung" jetzt eine Kachel auf der Startseite.
            Section {
                // v1.10.147: Upload-Anforderungen sichtbar/widerrufbar
                // machen — vorher gab's nur Erstellen, keine Listen-Ansicht.
                NavigationLink { UploadRequestsView() } label: {
                    Label("Upload-Anforderungen", systemImage: "tray.and.arrow.down").foregroundStyle(Theme.navyFg)
                }
                // v1.11.42: hierher getauscht — die Startseiten-Kachel zeigt
                // jetzt stattdessen Key-Store/"Lizenzverwaltung" (Marcus's
                // Wunsch, siehe BrowseRootView).
                NavigationLink { SharedWithMeView() } label: {
                    Label("Freigegeben für mich", systemImage: "person.crop.circle.badge.checkmark").foregroundStyle(Theme.navyFg)
                }
                NavigationLink { TrashView() } label: {
                    Label("Papierkorb", systemImage: "trash").foregroundStyle(Theme.danger2)
                }
                // v1.11.63: von der Startseite hierher verschoben — die
                // Startseiten-Kachel zeigt jetzt stattdessen "Benutzerverwaltung"
                // (admin-only, siehe BrowseRootView).
                NavigationLink { ActivityView() } label: {
                    Label("Aktivität", systemImage: "clock.fill").foregroundStyle(Theme.navyFg)
                }
            } header: { RSSectionHeader(title: "Dateien") }
                .listRowBackground(Theme.surface2)

            Section {
                NavigationLink { CertificatesView() } label: {
                    Label("Meine Zertifikate", systemImage: "seal").foregroundStyle(Theme.navyFg)
                }
                NavigationLink { ContactsView() } label: {
                    Label("Adressbuch", systemImage: "person.crop.circle.badge.checkmark").foregroundStyle(Theme.navyFg)
                }
            } header: { RSSectionHeader(title: "Signaturen") }
                .listRowBackground(Theme.surface2)

            // v1.10.88: iOS-Parität — API-Tokens, Webhooks
            // (v1.10.126: Linksammlung als Startseiten-Kachel ausgelagert)
            // v1.11.63: admin-gated — Web hat das seit v1.10.93 bewusst vor
            // normalen Usern versteckt ("Admin/Power-User-Krams", Marcus's
            // Report: "ich kann als normaler User Domain sehen, sollte man
            // nicht — auch keine API-Tokens"). iOS zeigte es bislang jedem.
            if auth.isAdmin {
                Section {
                    NavigationLink { ApiTokensView() } label: {
                        Label("API-Tokens", systemImage: "key").foregroundStyle(Theme.navyFg)
                    }
                    NavigationLink { WebhooksView() } label: {
                        Label("Webhooks", systemImage: "bolt.horizontal").foregroundStyle(Theme.navyFg)
                    }
                } header: { RSSectionHeader(title: "Wissen & Automatisierung") }
                    .listRowBackground(Theme.surface2)
            }

            // v1.10.165: AI-Consent-Toggle für Widerruf (Apple 5.1.1(i)).
            Section {
                Toggle(isOn: Binding(
                    get: { auth.aiConsented == true },
                    set: { newVal in Task { await auth.setAiConsent(newVal) } }
                )) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("KI-Verarbeitung erlaubt").font(TFont.bodyM).foregroundStyle(Theme.textPrimary)
                        if let info = auth.aiProviderInfo, info.enabled {
                            Text("Provider: \(info.provider)\(info.model.map { " · \($0)" } ?? "")")
                                .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                        } else {
                            Text("Auf dieser Instanz ist KI nicht aktiviert.")
                                .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                        }
                    }
                }
                .tint(Theme.cyan)
            } header: {
                RSSectionHeader(title: "KI-Nutzung")
            } footer: {
                Text("Erforderlich für Chat mit Dateien, semantische Suche und intelligente Zusammenfassungen. Widerrufen deaktiviert diese Funktionen sofort. Details in der Datenschutzerklärung.")
            }
            .listRowBackground(Theme.surface2)

            Section {
                NavigationLink { TwoFactorSetupView() } label: {
                    Label("Zwei-Faktor-Anmeldung", systemImage: "lock.shield").foregroundStyle(Theme.navyFg)
                }
                // v1.10.82: App-Store-Blocker Apple 1.2 — Blockliste einsehbar.
                NavigationLink { BlocksView() } label: {
                    Label("Blockierte Nutzer", systemImage: "hand.raised").foregroundStyle(Theme.navyFg)
                }
            } header: { RSSectionHeader(title: "Sicherheit") }
                .listRowBackground(Theme.surface2)

            Section {
                LabeledContent("URL", value: auth.serverURL?.absoluteString ?? "")
                Button("Server ändern", action: auth.changeServer).tint(Theme.cyan)
            } header: { RSSectionHeader(title: "Server") }
                .listRowBackground(Theme.surface2)

            Section {
                Button(role: .destructive, action: auth.signOut) {
                    Label("Abmelden", systemImage: "rectangle.portrait.and.arrow.right")
                }
            }
            .listRowBackground(Theme.surface2)

            // v1.10.82: App-Store-Blocker Apple 5.1.1(v) — Account-Löschung
            // MUSS aus der App heraus möglich sein. Eigene Section damit sie
            // visuell klar getrennt vom normalen „Abmelden" steht.
            Section {
                NavigationLink { DeleteAccountView() } label: {
                    Label("Account löschen", systemImage: "trash")
                        .foregroundStyle(Theme.danger2)
                }
            } footer: {
                Text("Löscht deinen Account und alle deine Dateien unwiderruflich vom Server.")
                    .font(TFont.caption)
            }
            .listRowBackground(Theme.surface2)

            // v1.10.88: App-Store Round 3 — Privacy/Support/Impressum-Links.
            // Öffnen im System-Browser gegen die eigene Server-Instanz —
            // damit der User die Policy sieht die zu SEINEM Backend gehört.
            Section {
                if let base = auth.serverURL {
                    Link(destination: base.appendingPathComponent("privacy")) {
                        Label("Datenschutz", systemImage: "lock.doc").foregroundStyle(Theme.navyFg)
                    }
                    Link(destination: base.appendingPathComponent("support")) {
                        Label("Support & Hilfe", systemImage: "questionmark.circle").foregroundStyle(Theme.navyFg)
                    }
                    Link(destination: base.appendingPathComponent("imprint")) {
                        Label("Impressum", systemImage: "info.circle").foregroundStyle(Theme.navyFg)
                    }
                }
            } header: { RSSectionHeader(title: "Rechtliches") }
                .listRowBackground(Theme.surface2)

            Section {
                HStack {
                    Text("NimShare iOS").font(TFont.bodyM).foregroundStyle(Theme.textPrimary)
                    Spacer()
                    Text(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
                        .font(TFont.bodyM).foregroundStyle(Theme.textSecondary)
                }
            }
            .listRowBackground(Theme.surface2)
        }
        .scrollContentBackground(.hidden)
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle("Einstellungen")
        .alert("Sprache geändert", isPresented: $showRestartHint) {
            Button("OK", role: .cancel) {}
        } message: {
            Text("Die App-Oberfläche wechselt beim nächsten Start in die neue Sprache. Server-seitige Inhalte (z. B. E-Mails) nutzen die neue Sprache sofort.")
        }
    }

    /// v1.11.63: persistiert serverseitig (User.PreferredCulture) UND setzt
    /// das iOS-Sprach-Override (AppleLanguages) — Apps können ihre eigene
    /// Bundle-Lokalisierung nicht live umschalten, erst nach Neustart.
    private func setCulture(_ code: String) async {
        guard let api = auth.api else { return }
        cultureBusy = true; cultureError = nil; defer { cultureBusy = false }
        do {
            let updated = try await api.setPreferredCulture(code)
            auth.user = updated
            UserDefaults.standard.set([code], forKey: "AppleLanguages")
            showRestartHint = true
        } catch let ex { cultureError = ex.localizedDescription }
    }
}

struct AvatarView: View {
    let user: UserDto?
    let size: CGFloat

    var body: some View {
        Group {
            if let urlStr = user?.avatarUrl, let url = fullURL(urlStr) {
                AsyncImage(url: url) { phase in
                    switch phase {
                    case .success(let img): img.resizable().scaledToFill()
                    default: initials
                    }
                }
            } else {
                initials
            }
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
        .overlay(Circle().stroke(.white.opacity(0.6), lineWidth: 2))
    }

    private var initials: some View {
        let name = user?.displayName ?? "?"
        let parts = name.split(separator: " ").compactMap(\.first).map(String.init)
        let letters = parts.prefix(2).joined().uppercased()
        return ZStack {
            Circle().fill(Color.hashTint(user?.email ?? name))
            Text(letters.isEmpty ? "?" : letters)
                .font(.system(size: size * 0.4, weight: .semibold))
                .foregroundStyle(.white)
        }
    }

    private func fullURL(_ s: String) -> URL? {
        if s.hasPrefix("http") { return URL(string: s) }
        // v1.10.79: totes Root-VC-Lookup entfernt — hatte nur `_ = base`
        // und diente keinem Zweck. Relative URLs werden direkt gegen den
        // konfigurierten Server aufgelöst.
        guard let baseStr = UserDefaults.standard.string(forKey: "nimshare.serverURL"),
              let baseURL = URL(string: baseStr) else { return nil }
        return URL(string: s, relativeTo: baseURL)
    }
}
