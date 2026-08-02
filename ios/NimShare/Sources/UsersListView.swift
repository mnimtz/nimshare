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
    @State private var showAdd = false

    private var pendingInvitations: [NimShareAPI.InvitationDto] {
        invitations.filter { $0.usedAt == nil && $0.revokedAt == nil }
    }

    var body: some View {
        List {
            if let e = error {
                Section { Text(e).foregroundStyle(Theme.danger2) }
            }
            if !pendingInvitations.isEmpty {
                Section("Ausstehende Einladungen") {
                    ForEach(pendingInvitations) { inv in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(inv.displayName).font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                            Text(inv.email).font(TFont.caption).foregroundStyle(Theme.textSecondary)
                            Text("Läuft ab \(inv.expiresAt.formatted(date: .abbreviated, time: .omitted))")
                                .font(TFont.caption).foregroundStyle(Theme.textTertiary)
                        }
                        .padding(.vertical, 2)
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                        .swipeActions {
                            Button("Widerrufen", role: .destructive) { Task { await revoke(inv) } }
                            Button("Erneut senden") { Task { await resend(inv) } }.tint(Theme.cyan)
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
                            ZStack {
                                Circle().fill(Theme.navy.opacity(0.12)).frame(width: 34, height: 34)
                                Image(systemName: "person.fill")
                                    .font(.system(size: 14, weight: .semibold))
                                    .foregroundStyle(Theme.navyFg)
                            }
                            VStack(alignment: .leading, spacing: 2) {
                                HStack(spacing: 6) {
                                    Text(u.displayName).font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                                    if u.role == "Admin" {
                                        Chip(text: "Admin", color: Theme.navyFg, bg: Theme.navy.opacity(0.12))
                                    }
                                    if !u.isActive {
                                        Text("Deaktiviert").font(TFont.caption).foregroundStyle(Theme.danger2)
                                    }
                                }
                                Text(u.email).font(TFont.caption).foregroundStyle(Theme.textSecondary)
                            }
                            Spacer()
                        }
                    }
                    .padding(.vertical, 2)
                    .listRowBackground(Theme.surface2)
                    .listRowSeparator(.hidden)
                    .swipeActions {
                        Button(u.isActive ? "Deaktivieren" : "Aktivieren") { Task { await toggleActive(u) } }
                            .tint(u.isActive ? Theme.danger2 : Theme.success2)
                    }
                }
            }
        }
        .scrollContentBackground(.hidden)
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle("Benutzerverwaltung")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                // v1.11.65: Marcus's Feedback — bisher gab es nur "Einladen"
                // (E-Mail-Link), aber kein direktes Anlegen mit selbst
                // gesetztem Passwort (Web hat das seit je unter
                // /settings/users/create, iOS hatte dafür nie eine JSON-API).
                Menu {
                    Button { showInvite = true } label: { Label("Einladen", systemImage: "envelope") }
                    Button { showAdd = true } label: { Label("Hinzufügen", systemImage: "person.badge.plus") }
                } label: {
                    Image(systemName: "plus")
                }
            }
        }
        .sheet(isPresented: $showInvite) {
            InviteUserSheet(onSent: { Task { await load() } })
        }
        .sheet(isPresented: $showAdd) {
            AddUserSheet(onCreated: { Task { await load() } })
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
                if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.bgGradient.ignoresSafeArea())
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

/// v1.11.65: direktes Anlegen mit selbst gesetztem Passwort — Pendant zu
/// `InviteUserSheet`, für den Fall dass kein Einladungs-E-Mail nötig/gewollt
/// ist (Parität zum Web-Formular "/settings/users/create").
struct AddUserSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let onCreated: () -> Void

    @State private var email = ""
    @State private var displayName = ""
    @State private var password = ""
    @State private var role = "User"
    @State private var busy = false
    @State private var error: String?

    private var canSubmit: Bool {
        !email.trimmingCharacters(in: .whitespaces).isEmpty && password.count >= 8 && !busy
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Benutzer") {
                    TextField("E-Mail", text: $email)
                        .keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                    TextField("Name (optional)", text: $displayName)
                    SecureField("Passwort", text: $password)
                        .textContentType(.newPassword)
                    Picker("Rolle", selection: $role) {
                        Text("Benutzer").tag("User")
                        Text("Admin").tag("Admin")
                    }
                }
                Section {
                    Text("Mindestens 8 Zeichen. Der Benutzer kann sich damit sofort anmelden — es wird keine Einladungs-E-Mail versendet.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.bgGradient.ignoresSafeArea())
            .navigationTitle("Benutzer hinzufügen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Hinzufügen") { Task { await create() } }
                        .disabled(!canSubmit)
                }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func create() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            _ = try await api.createUser(.init(email: email, displayName: displayName, password: password, role: role))
            onCreated()
            dismiss()
        } catch let ex { error = ex.localizedDescription }
    }
}
