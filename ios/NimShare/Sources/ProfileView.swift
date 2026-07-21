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
        // Relative path — resolve against configured server.
        if let base = (UIApplication.shared.connectedScenes
            .compactMap { ($0 as? UIWindowScene)?.windows.first?.rootViewController }
            .first as Any?),
           let baseStr = UserDefaults.standard.string(forKey: "nimshare.serverURL"),
           let baseURL = URL(string: baseStr) {
            _ = base
            return URL(string: s, relativeTo: baseURL)
        }
        return nil
    }
}
