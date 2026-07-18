import SwiftUI

/// Sheet for granting access to a file or folder to a specific user or group.
/// Mirrors the web modal at Views/Browse/Browse.cshtml.
struct DirectShareSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    enum Target: Hashable {
        case file(UUID)
        case folder(UUID)
    }

    let target: Target
    let itemName: String

    @State private var kind: PickerKind = .user
    @State private var permission: DirectSharePermission = .read

    @State private var userQuery = ""
    @State private var userMatches: [DirectShareUserOption] = []
    @State private var selectedUser: DirectShareUserOption?
    /// The in-flight user-search task so a new keystroke can cancel the
    /// previous request and prevent stale matches from overwriting fresh ones.
    @State private var searchTask: Task<Void, Never>?

    @State private var groups: [DirectShareGroupOption] = []
    @State private var selectedGroup: DirectShareGroupOption?

    @State private var shares: [DirectShareDto] = []
    @State private var loading = false
    @State private var error: String?

    enum PickerKind: CaseIterable, Identifiable {
        case user, group
        var id: Self { self }
        var localized: LocalizedStringKey {
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
                    Text(itemName).font(.body.weight(.medium)).lineLimit(2)
                } header: {
                    Text(target.isFile ? "Datei" : "Ordner")
                }

                Section("Neue Berechtigung") {
                    Picker("Typ", selection: $kind) {
                        ForEach(PickerKind.allCases) { k in
                            Text(k.localized).tag(k)
                        }
                    }
                    .pickerStyle(.segmented)

                    if kind == .user {
                        TextField("Nutzer suchen…", text: $userQuery)
                            .autocorrectionDisabled()
                            .textInputAutocapitalization(.never)
                            .onChange(of: userQuery) { _, new in
                                searchTask?.cancel()
                                if selectedUser?.displayName != new {
                                    selectedUser = nil
                                }
                                searchTask = Task { await searchUsers(new) }
                            }
                        if !userMatches.isEmpty {
                            ForEach(userMatches) { u in
                                Button {
                                    selectedUser = u
                                    userQuery = u.displayName
                                    userMatches = []
                                } label: {
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(u.displayName).foregroundStyle(.primary)
                                        Text(u.email).font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                            }
                        }
                    } else {
                        if groups.isEmpty {
                            Text("Keine Gruppen verfügbar").foregroundStyle(.secondary)
                        } else {
                            Picker("Gruppe", selection: $selectedGroup) {
                                Text("—").tag(DirectShareGroupOption?.none)
                                ForEach(groups) { g in
                                    Text(g.name).tag(Optional(g))
                                }
                            }
                        }
                    }

                    Picker("Berechtigung", selection: $permission) {
                        ForEach(DirectSharePermission.allCases) { p in
                            Text(p == .read ? "Lesen" : "Schreiben").tag(p)
                        }
                    }

                    Button("Hinzufügen") { Task { await grant() } }
                        .disabled(!canGrant)
                }

                Section("Bereits freigegeben") {
                    if shares.isEmpty {
                        Text("Noch niemand.").foregroundStyle(.secondary)
                    } else {
                        ForEach(shares) { s in
                            HStack {
                                Image(systemName: s.isGroup ? "person.3.fill" : "person.crop.circle.fill")
                                    .foregroundStyle(Theme.tungstenBlue)
                                    .frame(width: 24)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(s.displayName)
                                    Text(s.permissionEnum == .write ? "Schreibrechte" : "Nur lesen")
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                Button(role: .destructive) {
                                    Task { await revoke(s.id) }
                                } label: {
                                    Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }
                }

                if let e = error {
                    Text(e).foregroundStyle(Theme.warnRed).font(.footnote)
                }
            }
            .navigationTitle("Freigeben an…")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Fertig") { dismiss() }
                }
            }
            .task {
                await loadGroups()
                await loadShares()
            }
            .overlay { if loading { ProgressView() } }
        }
    }

    private var canGrant: Bool {
        (kind == .user && selectedUser != nil) || (kind == .group && selectedGroup != nil)
    }

    /// Debounced user search. Cancelled by the caller when a new keystroke
    /// lands, so out-of-order responses can't leak into userMatches.
    private func searchUsers(_ q: String) async {
        // Debounce so we don't fire on every keystroke.
        try? await Task.sleep(nanoseconds: 250_000_000)
        if Task.isCancelled { return }
        guard let api = auth.api, q.count >= 2 else {
            if !Task.isCancelled { userMatches = [] }
            return
        }
        do {
            let hits = try await api.searchShareableUsers(q)
            if !Task.isCancelled { userMatches = hits }
        } catch { /* ignore — search input is transient */ }
    }

    private func loadGroups() async {
        guard let api = auth.api else { return }
        do { groups = try await api.listShareableGroups() }
        catch { }
    }

    private func loadShares() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do {
            switch target {
            case .file(let id): shares = try await api.directShares(forFile: id)
            case .folder(let id): shares = try await api.directShares(forFolder: id)
            }
        } catch let ex { error = ex.localizedDescription }
    }

    private func grant() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do {
            switch target {
            case .file(let id):
                try await api.createDirectShare(
                    fileId: id,
                    userId: kind == .user ? selectedUser?.id : nil,
                    groupId: kind == .group ? selectedGroup?.id : nil,
                    permission: permission)
            case .folder(let id):
                try await api.createDirectShare(
                    folderId: id,
                    userId: kind == .user ? selectedUser?.id : nil,
                    groupId: kind == .group ? selectedGroup?.id : nil,
                    permission: permission)
            }
            userQuery = ""; selectedUser = nil
            await loadShares()
        } catch let ex { error = ex.localizedDescription }
    }

    private func revoke(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.revokeDirectShare(id); await loadShares() }
        catch let ex { error = ex.localizedDescription }
    }
}

private extension DirectShareSheet.Target {
    var isFile: Bool {
        if case .file = self { return true }
        return false
    }
}
