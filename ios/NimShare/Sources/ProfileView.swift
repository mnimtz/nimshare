import SwiftUI

struct ProfileView: View {
    @EnvironmentObject var auth: AuthStore

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
                Section("Storage quota") {
                    LabeledContent("Quota", value: ByteCountFormatter.string(fromByteCount: u.quotaBytes, countStyle: .file))
                    LabeledContent("Language", value: u.preferredCulture)
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

            Section {
                HStack {
                    Text("NimShare iOS")
                    Spacer()
                    Text(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
                        .foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle("Profil")
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
