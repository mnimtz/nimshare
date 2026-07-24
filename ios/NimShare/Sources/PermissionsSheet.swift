import SwiftUI

/// v1.10.104 (Stage 2 „Windows-ACL"): Sheet für einen Public-Ordner mit
/// Privacy-Toggle + Grants-Liste. Zusammenpiel mit dem Web-Modal in
/// Views/Browse/Browse.cshtml und dem existierenden DirectShareSheet.
struct PermissionsSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    let folderId: UUID
    let folderName: String

    @State private var isPrivate = false
    // v1.10.108: gespiegelter Server-Zustand. Der onChange(of: isPrivate)
    // feuert auch bei programmatischen Änderungen (load-Ergebnis, Fehler-
    // Rollback) — ohne den Guard gegen serverIsPrivate entstand eine
    // endlose PATCH-403-Schleife, sobald ein Nur-Lese-User das Sheet
    // eines privaten Ordners öffnete.
    @State private var serverIsPrivate = false
    @State private var canManage = false
    @State private var userGrants: [FolderPermissionUserGrant] = []
    @State private var groupGrants: [FolderPermissionGroupGrant] = []
    @State private var loading = true
    @State private var error: String?

    @State private var kind: PickerKind = .user
    @State private var permission: DirectSharePermission = .read

    @State private var userQuery = ""
    @State private var userMatches: [DirectShareUserOption] = []
    @State private var selectedUser: DirectShareUserOption?
    @State private var searchTask: Task<Void, Never>?

    @State private var groups: [DirectShareGroupOption] = []
    @State private var selectedGroup: DirectShareGroupOption?

    enum PickerKind: CaseIterable, Identifiable {
        case user, group
        var id: Self { self }
        var label: LocalizedStringKey {
            switch self {
            case .user: return "Nutzer"
            case .group: return "Gruppe"
            }
        }
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text(folderName).font(.body.weight(.medium)).lineLimit(2)
                } header: {
                    Text("Öffentlicher Ordner")
                } footer: {
                    Text("Schalt den Ordner auf privat und nur die unten aufgeführten Nutzer und Gruppen sehen ihn noch — alle anderen sehen ihn weder in der Ordner-Liste, in der Suche noch im AI-Chat.")
                }

                Section {
                    Toggle(isOn: $isPrivate) {
                        Label("Nur für explizit Berechtigte sichtbar", systemImage: "lock.fill")
                    }
                    .disabled(!canManage || loading)
                    .onChange(of: isPrivate) { _, newValue in
                        // Nur echte User-Umschaltungen an den Server senden.
                        guard newValue != serverIsPrivate else { return }
                        Task { await togglePrivacy(newValue) }
                    }
                } header: {
                    Text("Sichtbarkeit")
                }

                Section("Berechtigte hinzufügen") {
                    Picker("Typ", selection: $kind) {
                        ForEach(PickerKind.allCases) { k in
                            Text(k.label).tag(k)
                        }
                    }
                    .pickerStyle(.segmented)

                    if kind == .user {
                        TextField("Nach Nutzer suchen…", text: $userQuery)
                            .autocorrectionDisabled()
                            .onChange(of: userQuery) { _, newValue in
                                searchTask?.cancel()
                                searchTask = Task { await searchUsers(newValue) }
                            }
                        if !userMatches.isEmpty {
                            ForEach(userMatches) { u in
                                Button {
                                    selectedUser = u
                                    userQuery = u.displayName
                                    userMatches = []
                                } label: {
                                    HStack {
                                        Image(systemName: "person.crop.circle")
                                        VStack(alignment: .leading) {
                                            Text(u.displayName)
                                            Text(u.email).font(.caption).foregroundStyle(.secondary)
                                        }
                                    }
                                }
                            }
                        }
                    } else {
                        if groups.isEmpty {
                            Text("Keine Gruppen verfügbar.").foregroundStyle(.secondary)
                        } else {
                            Picker("Gruppe", selection: $selectedGroup) {
                                Text("—").tag(Optional<DirectShareGroupOption>.none)
                                ForEach(groups) { g in
                                    Text(g.name).tag(Optional(g))
                                }
                            }
                        }
                    }

                    Picker("Rechte", selection: $permission) {
                        ForEach(DirectSharePermission.allCases) { p in
                            Text(p.localized).tag(p)
                        }
                    }
                    .pickerStyle(.segmented)

                    Button {
                        Task { await addGrant() }
                    } label: {
                        Label("Hinzufügen", systemImage: "plus.circle.fill")
                    }
                    .disabled(!canManage || loading || !canAdd)
                }

                Section("Berechtigt") {
                    if userGrants.isEmpty && groupGrants.isEmpty {
                        Text("Noch niemand berechtigt.").foregroundStyle(.secondary)
                    }
                    ForEach(userGrants) { g in
                        grantRow(icon: "person.crop.circle", label: g.label, perm: g.permissionEnum) {
                            Task { await revoke(g.id) }
                        }
                    }
                    ForEach(groupGrants) { g in
                        grantRow(icon: "person.3.fill", label: g.label, perm: g.permissionEnum) {
                            Task { await revoke(g.id) }
                        }
                    }
                }
            }
            .navigationTitle("🔒 Berechtigungen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
            .task { await load() }
            // v1.10.151: write-back-Binding statt .constant — sonst kann
            // SwiftUI den Alert bei Framework-Dismiss (Scene-Change,
            // iPad-Drag-to-Dismiss) nicht schließen und er würde sofort
            // wieder erscheinen.
            .alert("Fehler", isPresented: Binding(
                get: { error != nil }, set: { if !$0 { error = nil } })) {
                Button("OK", role: .cancel) { error = nil }
            } message: { Text(error ?? "") }
        }
    }

    private var canAdd: Bool {
        switch kind {
        case .user: return selectedUser != nil
        case .group: return selectedGroup != nil
        }
    }

    private func grantRow(icon: String, label: String, perm: DirectSharePermission, remove: @escaping () -> Void) -> some View {
        HStack {
            Image(systemName: icon).foregroundStyle(Theme.tungstenBlue)
            Text(label)
            Spacer()
            Text(perm.localized).font(.caption).padding(.horizontal, 8).padding(.vertical, 3)
                .background(perm == .write ? Color.orange.opacity(0.2) : Color.gray.opacity(0.2))
                .clipShape(Capsule())
            if canManage {
                Button(role: .destructive) { remove() } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.plain)
            }
        }
    }

    // MARK: - Actions

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do {
            let d = try await api.folderPermissions(id: folderId)
            serverIsPrivate = d.isPrivate
            isPrivate = d.isPrivate
            canManage = d.canManage
            userGrants = d.userGrants
            groupGrants = d.groupGrants
            groups = (try? await api.listShareableGroups()) ?? []
        } catch let e as ApiError {
            error = e.localizedDescription
        } catch let ex {
            error = ex.localizedDescription
        }
    }

    private func togglePrivacy(_ wanted: Bool) async {
        guard let api = auth.api else { return }
        do {
            let result = try await api.setFolderPrivacy(id: folderId, isPrivate: wanted)
            serverIsPrivate = result
            isPrivate = result
        } catch let e as ApiError {
            error = e.localizedDescription
            // Rollback auf den Server-Zustand — der onChange-Guard
            // (newValue == serverIsPrivate) verhindert ein Re-Fire.
            isPrivate = serverIsPrivate
        } catch let ex {
            error = ex.localizedDescription
            isPrivate = serverIsPrivate
        }
    }

    private func searchUsers(_ q: String) async {
        guard let api = auth.api, q.count >= 2 else { userMatches = []; return }
        // v1.10.148: Bug #8 — Debounce eingeführt (analog DirectShareSheet).
        // onChange feuert pro Tastendruck einen Task; der vorige wird zwar
        // gecancelt, aber ohne Sleep-Puffer ging trotzdem pro Zeichen ein
        // Server-Roundtrip raus. Jetzt: 250ms warten und nach Wach werden
        // prüfen, ob der Task inzwischen cancelled wurde (User tippt weiter).
        try? await Task.sleep(nanoseconds: 250_000_000)
        if Task.isCancelled { return }
        do {
            let items = try await api.searchShareableUsers(q)
            if !Task.isCancelled { userMatches = items }
        } catch {}
    }

    private func addGrant() async {
        guard let api = auth.api else { return }
        do {
            switch kind {
            case .user:
                guard let u = selectedUser else { return }
                try await api.createDirectShare(folderId: folderId, userId: u.id, permission: permission)
                selectedUser = nil; userQuery = ""; userMatches = []
            case .group:
                guard let g = selectedGroup else { return }
                try await api.createDirectShare(folderId: folderId, groupId: g.id, permission: permission)
                selectedGroup = nil
            }
            await load()
        } catch let e as ApiError {
            error = e.localizedDescription
        } catch let ex {
            error = ex.localizedDescription
        }
    }

    private func revoke(_ id: UUID) async {
        guard let api = auth.api else { return }
        do {
            try await api.revokeDirectShare(id)
            await load()
        } catch let e as ApiError {
            error = e.localizedDescription
        } catch let ex {
            error = ex.localizedDescription
        }
    }
}
