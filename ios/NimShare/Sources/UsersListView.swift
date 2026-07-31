import SwiftUI

/// v1.11.63: Benutzerverwaltung — Admin-only, 1:1-Parität mit /settings/users
/// im Web (Marcus's expliziter Wunsch, statt einer nur-Web-Funktion). Liste
/// der Benutzer + ausstehenden Einladungen, Tap auf einen Benutzer → UserEditView.
struct UsersListView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var users: [NimShareAPI.UserListItemDto] = []
    @State private var invitations: [NimShareAPI.InvitationDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var showInvite = false

    private var pendingInvitations: [NimShareAPI.InvitationDto] {
        invitations.filter { $0.usedAt == nil && $0.revokedAt == nil }
    }

    var body: some View {
        List {
            if let e = error {
                Section { Text(e).foregroundStyle(Theme.warnRed) }
            }
            if !pendingInvitations.isEmpty {
                Section("Ausstehende Einladungen") {
                    ForEach(pendingInvitations) { inv in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(inv.displayName).font(.subheadline.weight(.medium))
                            Text(inv.email).font(.caption).foregroundStyle(.secondary)
                            Text("Läuft ab \(inv.expiresAt.formatted(date: .abbreviated, time: .omitted))")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                        .swipeActions {
                            Button("Widerrufen", role: .destructive) { Task { await revoke(inv) } }
                            Button("Erneut senden") { Task { await resend(inv) } }.tint(Theme.tungstenBlue)
                        }
                    }
                }
            }
            Section("Benutzer (\(users.count))") {
                ForEach(users) { u in
                    NavigationLink {
                        UserEditView(userId: u.id, onSaved: { Task { await load() } }, onDeleted: { Task { await load() } })
                    } label: {
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                HStack(spacing: 6) {
                                    Text(u.displayName).font(.subheadline.weight(.medium))
                                    if u.role == "Admin" {
                                        Text("Admin").font(.caption2).padding(.horizontal, 6).padding(.vertical, 2)
                                            .background(Theme.tungstenBlue.opacity(0.15))
                                            .foregroundStyle(Theme.tungstenBlue).clipShape(Capsule())
                                    }
                                    if !u.isActive {
                                        Text("Deaktiviert").font(.caption2).foregroundStyle(Theme.warnRed)
                                    }
                                }
                                Text(u.email).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                        }
                    }
                    .swipeActions {
                        Button(u.isActive ? "Deaktivieren" : "Aktivieren") { Task { await toggleActive(u) } }
                            .tint(u.isActive ? Theme.warnRed : .green)
                    }
                }
            }
        }
        .navigationTitle("Benutzerverwaltung")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showInvite = true } label: { Image(systemName: "person.badge.plus") }
            }
        }
        .sheet(isPresented: $showInvite) {
            InviteUserSheet(onSent: { Task { await load() } })
        }
        .task { await load() }
        .refreshable { await load() }
        .overlay { if loading && users.isEmpty { ProgressView() } }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do {
            async let u = api.listUsers()
            async let i = api.listInvitations()
            users = try await u
            invitations = try await i
        } catch is CancellationError { /* pull-refresh/tab-switch */ }
        catch let ex { error = ex.localizedDescription }
    }

    private func toggleActive(_ u: NimShareAPI.UserListItemDto) async {
        guard let api = auth.api else { return }
        do { _ = try await api.toggleUserActive(u.id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
    private func revoke(_ inv: NimShareAPI.InvitationDto) async {
        guard let api = auth.api else { return }
        do { try await api.revokeInvitation(inv.id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
    private func resend(_ inv: NimShareAPI.InvitationDto) async {
        guard let api = auth.api else { return }
        do { try await api.resendInvitation(inv.id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
}

struct InviteUserSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let onSent: () -> Void

    @State private var email = ""
    @State private var displayName = ""
    @State private var role = "User"
    @State private var language = "de"
    @State private var busy = false
    @State private var error: String?
    @State private var manualUrl: String?

    private let languages = [("de", "Deutsch"), ("en", "English"), ("fr", "Français"),
                              ("it", "Italiano"), ("es", "Español"), ("nl", "Nederlands")]

    var body: some View {
        NavigationStack {
            Form {
                Section("Einladung") {
                    TextField("E-Mail", text: $email)
                        .keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                    TextField("Name (optional)", text: $displayName)
                    Picker("Rolle", selection: $role) {
                        Text("Benutzer").tag("User")
                        Text("Admin").tag("Admin")
                    }
                    Picker("Sprache der Einladung", selection: $language) {
                        ForEach(languages, id: \.0) { code, name in Text(name).tag(code) }
                    }
                }
                if let m = manualUrl {
                    Section("E-Mail-Versand fehlgeschlagen — Link manuell weitergeben") {
                        Text(m).font(.footnote).textSelection(.enabled)
                    }
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
            }
            .navigationTitle("Benutzer einladen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Einladen") { Task { await send() } }
                        .disabled(busy || email.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func send() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            let result = try await api.inviteUser(.init(email: email, displayName: displayName, role: role, language: language))
            if result.emailSent {
                onSent()
                dismiss()
            } else {
                manualUrl = result.manualUrl
                error = result.error
            }
        } catch let ex { error = ex.localizedDescription }
    }
}
