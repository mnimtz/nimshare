import SwiftUI

/// v1.11.63: einheitliches Edit-Formular für einen Benutzer — 1:1-Parität mit
/// dem Web-Edit-Screen (/settings/users/{id}): Stammdaten, Rolle/Status,
/// Kontingent, Öffentliche-Bibliothek-Rechte, Gruppen, Passwort-Reset, Löschen.
struct UserEditView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let userId: UUID
    let onSaved: () -> Void
    let onDeleted: () -> Void

    @State private var detail: NimShareAPI.UserDetailDto?
    @State private var allGroups: [NimShareAPI.GroupListItemDto] = []

    @State private var displayName = ""
    @State private var email = ""
    @State private var role = "User"
    @State private var quotaGb = 10
    @State private var isActive = true
    @State private var publicCanRead = true
    @State private var publicCanWrite = true
    @State private var publicCanDelete = false
    @State private var selectedGroupIds: Set<UUID> = []
    @State private var newPassword = ""

    @State private var loading = true
    @State private var busy = false
    @State private var error: String?
    @State private var showDeleteConfirm = false

    private var isSelf: Bool { userId == auth.user?.id }

    var body: some View {
        Form {
            if loading {
                ProgressView()
            } else {
                Section("Stammdaten") {
                    TextField("Name", text: $displayName)
                    TextField("E-Mail", text: $email)
                        .keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                Section("Rolle & Status") {
                    Picker("Rolle", selection: $role) {
                        Text("Benutzer").tag("User")
                        Text("Admin").tag("Admin")
                    }
                    Toggle("Aktiv", isOn: $isActive).disabled(isSelf)
                }
                if isSelf {
                    Text("Du kannst deinen eigenen Account nicht deaktivieren oder löschen.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Section("Speicher") {
                    Stepper("Kontingent: \(quotaGb) GB", value: $quotaGb, in: 1...10240)
                }
                Section("Öffentliche Bibliothek") {
                    Toggle("Lesen", isOn: $publicCanRead)
                    Toggle("Schreiben", isOn: $publicCanWrite)
                    Toggle("Löschen", isOn: $publicCanDelete)
                }
                if !allGroups.isEmpty {
                    Section("Gruppen") {
                        ForEach(allGroups) { g in
                            Button {
                                if selectedGroupIds.contains(g.id) { selectedGroupIds.remove(g.id) }
                                else { selectedGroupIds.insert(g.id) }
                            } label: {
                                HStack {
                                    Text(g.name).foregroundStyle(.primary)
                                    Spacer()
                                    if selectedGroupIds.contains(g.id) {
                                        Image(systemName: "checkmark").foregroundStyle(Theme.navyFg)
                                    }
                                }
                            }
                        }
                    }
                }
                Section("Passwort zurücksetzen (optional)") {
                    SecureField("Neues Passwort (min. 8 Zeichen)", text: $newPassword)
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
                if !isSelf {
                    Section {
                        Button("Benutzer löschen", role: .destructive) { showDeleteConfirm = true }
                    }
                }
            }
        }
        .scrollContentBackground(.hidden)
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle(detail?.displayName ?? "Benutzer")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button("Speichern") { Task { await save() } }.disabled(busy || loading)
            }
        }
        .task { await load() }
        .overlay { if busy { ProgressView() } }
        .alert("Benutzer löschen?", isPresented: $showDeleteConfirm) {
            Button("Abbrechen", role: .cancel) {}
            Button("Löschen", role: .destructive) { Task { await delete() } }
        } message: {
            Text("Dies kann nicht rückgängig gemacht werden.")
        }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do {
            async let d = api.getUser(userId)
            async let g = api.listAllGroups()
            let detailResult = try await d
            allGroups = try await g
            detail = detailResult
            displayName = detailResult.displayName
            email = detailResult.email
            role = detailResult.role
            quotaGb = max(1, Int(detailResult.quotaBytes / 1024 / 1024 / 1024))
            isActive = detailResult.isActive
            publicCanRead = detailResult.publicCanRead
            publicCanWrite = detailResult.publicCanWrite
            publicCanDelete = detailResult.publicCanDelete
            selectedGroupIds = Set(detailResult.groups.map(\.groupId))
        } catch let ex { error = ex.localizedDescription }
    }

    private func save() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            try await api.updateUser(userId, .init(
                displayName: displayName, email: email, role: role, quotaGb: quotaGb, isActive: isActive,
                groupIds: Array(selectedGroupIds), newPassword: newPassword.isEmpty ? nil : newPassword,
                publicCanRead: publicCanRead, publicCanWrite: publicCanWrite, publicCanDelete: publicCanDelete))
            newPassword = ""
            onSaved()
            dismiss()
        } catch let ex { error = ex.localizedDescription }
    }

    private func delete() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            try await api.deleteUser(userId)
            onDeleted()
            dismiss()
        } catch let ex { error = ex.localizedDescription }
    }
}
