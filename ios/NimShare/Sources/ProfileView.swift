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

    var body: some View {
        Form {
            Section {
                HStack(spacing: 16) {
                    AvatarView(user: auth.user, size: 72)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(auth.user?.displayName ?? "").font(.title3.weight(.semibold))
                        Text(auth.user?.email ?? "").font(.footnote).foregroundStyle(.secondary)
                        if let role = auth.user?.role {
                            Text(role).font(.caption).foregroundStyle(Theme.tungstenBlue)
                        }
                    }
                }
                .padding(.vertical, 4)
            }

            if let u = auth.user {
                // v1.10.149: EN/DE-Mix behoben — vorher hartkodiertes
                // „Storage quota"/„Quota"/„Language" mitten in deutscher
                // Section-Umgebung.
                Section("Speicher") {
                    LabeledContent("Kontingent", value: ByteCountFormatter.string(fromByteCount: u.quotaBytes, countStyle: .file))
                    Picker("Sprache", selection: Binding(
                        get: { u.preferredCulture },
                        set: { newCode in Task { await setCulture(newCode) } }
                    )) {
                        ForEach(cultureOptions, id: \.0) { code, name in Text(name).tag(code) }
                    }
                    .disabled(cultureBusy)
                    if let e = cultureError { Text(e).font(.caption).foregroundStyle(Theme.warnRed) }
                }
            }

            // v1.10.126: Papierkorb von der Startseiten-Kachel hierher —
            // dafür ist „Linksammlung" jetzt eine Kachel auf der Startseite.
            Section("Dateien") {
                // v1.10.147: Upload-Anforderungen sichtbar/widerrufbar
                // machen — vorher gab's nur Erstellen, keine Listen-Ansicht.
                NavigationLink { UploadRequestsView() } label: {
                    Label("Upload-Anforderungen", systemImage: "tray.and.arrow.down")
                }
                // v1.11.42: hierher getauscht — die Startseiten-Kachel zeigt
                // jetzt stattdessen Key-Store/"Lizenzverwaltung" (Marcus's
                // Wunsch, siehe BrowseRootView).
                NavigationLink { SharedWithMeView() } label: {
                    Label("Freigegeben für mich", systemImage: "person.crop.circle.badge.checkmark")
                }
                NavigationLink { TrashView() } label: {
                    Label("Papierkorb", systemImage: "trash").foregroundStyle(Theme.warnRed)
                }
                // v1.11.63: von der Startseite hierher verschoben — die
                // Startseiten-Kachel zeigt jetzt stattdessen "Benutzerverwaltung"
                // (admin-only, siehe BrowseRootView).
                NavigationLink { ActivityView() } label: {
                    Label("Aktivität", systemImage: "clock.fill")
                }
            }

            Section("Signaturen") {
                NavigationLink { CertificatesView() } label: {
                    Label("Meine Zertifikate", systemImage: "seal")
                }
                NavigationLink { ContactsView() } label: {
                    Label("Adressbuch", systemImage: "person.crop.circle.badge.checkmark")
                }
            }

            // v1.10.88: iOS-Parität — API-Tokens, Webhooks
            // (v1.10.126: Linksammlung als Startseiten-Kachel ausgelagert)
            // v1.11.63: admin-gated — Web hat das seit v1.10.93 bewusst vor
            // normalen Usern versteckt ("Admin/Power-User-Krams", Marcus's
            // Report: "ich kann als normaler User Domain sehen, sollte man
            // nicht — auch keine API-Tokens"). iOS zeigte es bislang jedem.
            if auth.isAdmin {
                Section("Wissen & Automatisierung") {
                    NavigationLink { ApiTokensView() } label: {
                        Label("API-Tokens", systemImage: "key")
                    }
                    NavigationLink { WebhooksView() } label: {
                        Label("Webhooks", systemImage: "bolt.horizontal")
                    }
                }
            }

            // v1.10.165: AI-Consent-Toggle für Widerruf (Apple 5.1.1(i)).
            Section {
                Toggle(isOn: Binding(
                    get: { auth.aiConsented == true },
                    set: { newVal in Task { await auth.setAiConsent(newVal) } }
                )) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("KI-Verarbeitung erlaubt")
                        if let info = auth.aiProviderInfo, info.enabled {
                            Text("Provider: \(info.provider)\(info.model.map { " · \($0)" } ?? "")")
                                .font(.caption).foregroundStyle(.secondary)
                        } else {
                            Text("Auf dieser Instanz ist KI nicht aktiviert.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            } header: {
                Text("KI-Nutzung")
            } footer: {
                Text("Erforderlich für Chat mit Dateien, semantische Suche und intelligente Zusammenfassungen. Widerrufen deaktiviert diese Funktionen sofort. Details in der Datenschutzerklärung.")
            }

            Section("Sicherheit") {
                NavigationLink { TwoFactorSetupView() } label: {
                    Label("Zwei-Faktor-Anmeldung", systemImage: "lock.shield")
                }
                // v1.10.82: App-Store-Blocker Apple 1.2 — Blockliste einsehbar.
                NavigationLink { BlocksView() } label: {
                    Label("Blockierte Nutzer", systemImage: "hand.raised")
                }
            }

            Section("Server") {
                LabeledContent("URL", value: auth.serverURL?.absoluteString ?? "")
                Button("Server ändern", action: auth.changeServer)
            }

            Section {
                Button(role: .destructive, action: auth.signOut) {
                    Label("Abmelden", systemImage: "rectangle.portrait.and.arrow.right")
                }
            }

            // v1.10.82: App-Store-Blocker Apple 5.1.1(v) — Account-Löschung
            // MUSS aus der App heraus möglich sein. Eigene Section damit sie
            // visuell klar getrennt vom normalen „Abmelden" steht.
            Section {
                NavigationLink { DeleteAccountView() } label: {
                    Label("Account löschen", systemImage: "trash")
                        .foregroundStyle(Theme.warnRed)
                }
            } footer: {
                Text("Löscht deinen Account und alle deine Dateien unwiderruflich vom Server.")
                    .font(.caption)
            }

            // v1.10.88: App-Store Round 3 — Privacy/Support/Impressum-Links.
            // Öffnen im System-Browser gegen die eigene Server-Instanz —
            // damit der User die Policy sieht die zu SEINEM Backend gehört.
            Section("Rechtliches") {
                if let base = auth.serverURL {
                    Link(destination: base.appendingPathComponent("privacy")) {
                        Label("Datenschutz", systemImage: "lock.doc")
                    }
                    Link(destination: base.appendingPathComponent("support")) {
                        Label("Support & Hilfe", systemImage: "questionmark.circle")
                    }
                    Link(destination: base.appendingPathComponent("imprint")) {
                        Label("Impressum", systemImage: "info.circle")
                    }
                }
            }

            Section {
                HStack {
                    Text("NimShare iOS")
                    Spacer()
                    Text(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
                        .foregroundStyle(.secondary)
                }
            }
        }
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
