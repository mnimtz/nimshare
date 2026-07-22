import SwiftUI

/// v1.10.74: Adressbuch mit zwei Modi (Segmented Control).
///  - "Meine Kontakte": persönlich angelegte Contacts, editierbar
///  - "NimShare-User": alle aktiven User im System, read-only Directory
/// Suche filtert die aktive Sektion. Add-Button (+) legt nur in "Meine"
/// an — im Directory-Modus ausgeblendet (kein manuelles User-Anlegen).
struct ContactsView: View {
    @EnvironmentObject var auth: AuthStore

    enum Mode: String, CaseIterable, Identifiable {
        case mine, directory
        var id: Self { self }
        var label: String { self == .mine ? "Meine Kontakte" : "NimShare-User" }
    }

    @State private var mode: Mode = .mine
    @State private var myContacts: [ContactDto] = []
    @State private var directory: [DirectoryUserDto] = []
    @State private var searchText = ""
    @State private var loading = true
    @State private var error: String?
    @State private var showAdd = false
    // v1.10.113: Kontakt bearbeiten via Long-Press.
    @State private var editContact: ContactDto?
    // v1.10.82: pending State für Report/Block-Sheet
    @State private var pendingReportUser: (id: UUID, name: String)?

    var body: some View {
        VStack(spacing: 0) {
            Picker("Modus", selection: $mode) {
                ForEach(Mode.allCases) { m in
                    Text(m.label).tag(m)
                }
            }
            .pickerStyle(.segmented)
            .padding(.horizontal).padding(.top, 8)

            Group {
                if loading && (myContacts.isEmpty && directory.isEmpty) {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    switch mode {
                    case .mine: myList
                    case .directory: directoryList
                    }
                }
            }
        }
        .navigationTitle("Adressbuch")
        .toolbar {
            if mode == .mine {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { showAdd = true } label: { Image(systemName: "plus") }
                }
            }
        }
        .task { await loadAll() }
        .refreshable { await loadAll() }
        .sheet(isPresented: $showAdd) {
            AddContactSheet { Task { await loadMy() } }
        }
        // v1.10.113: Bearbeiten-Sheet (Long-Press auf „Meine Kontakte").
        .sheet(item: $editContact) { c in
            AddContactSheet(existing: c) { Task { await loadMy() } }
        }
        // v1.10.82: Report-Sheet für User-Meldungen aus dem Directory heraus.
        .sheet(item: Binding(
            get: { pendingReportUser.map { ReportContext(id: $0.id, name: $0.name) } },
            set: { if $0 == nil { pendingReportUser = nil } })
        ) { ctx in
            ReportSheet(subjectKind: .user, subjectId: ctx.id,
                        subjectLabel: ctx.name,
                        subjectOwnerUserId: ctx.id,
                        subjectOwnerName: ctx.name)
        }
        .alert("Fehler", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        } message: { Text(error ?? "") }
    }

    // Wrapper damit .sheet(item:) einen Identifiable typ bekommt.
    private struct ReportContext: Identifiable { let id: UUID; let name: String }

    private func block(_ userId: UUID, name: String) async {
        guard let api = auth.api else { return }
        do {
            try await api.blockUser(userId, reason: nil)
            // Directory-Liste neu laden → blockierter User verschwindet.
            await loadDirRaw()
        } catch let ex { error = ex.localizedDescription }
    }

    @ViewBuilder
    private var myList: some View {
        let filtered = myContacts.filter { searchText.isEmpty ||
            $0.name.localizedCaseInsensitiveContains(searchText) ||
            $0.email.localizedCaseInsensitiveContains(searchText) ||
            ($0.company ?? "").localizedCaseInsensitiveContains(searchText)
        }
        if filtered.isEmpty {
            ContentUnavailableView(
                myContacts.isEmpty ? "Noch kein Kontakt" : "Nichts gefunden",
                systemImage: "person.crop.circle",
                description: Text(myContacts.isEmpty
                    ? "Kontakte tauchen automatisch auf wenn du jemanden zum Signieren einlädst — du kannst auch manuell welche anlegen (+ oben rechts)."
                    : "Kein Kontakt passt zu \"\(searchText)\"."))
        } else {
            List {
                ForEach(filtered) { c in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(c.name).font(.body.weight(.semibold))
                            Spacer()
                            if c.useCount > 0 {
                                Text("\(c.useCount)×").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        Text(c.email).font(.caption.monospaced()).foregroundStyle(.secondary)
                        if let company = c.company, !company.isEmpty {
                            Text(company).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .padding(.vertical, 2)
                    .contentShape(Rectangle())
                    // v1.10.113: Long-Press → Bearbeiten/Löschen.
                    .contextMenu {
                        Button { editContact = c } label: { Label("Bearbeiten", systemImage: "pencil") }
                        Button(role: .destructive) { Task { await deleteContact(c.id) } } label: {
                            Label("Löschen", systemImage: "trash")
                        }
                    }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) {
                            Task { await deleteContact(c.id) }
                        } label: { Label("Löschen", systemImage: "trash") }
                        Button { editContact = c } label: { Label("Bearbeiten", systemImage: "pencil") }
                            .tint(Theme.tungstenBlue)
                    }
                }
            }
            .searchable(text: $searchText, prompt: "Suchen")
        }
    }

    @ViewBuilder
    private var directoryList: some View {
        let filtered = directory.filter { searchText.isEmpty ||
            $0.name.localizedCaseInsensitiveContains(searchText) ||
            $0.email.localizedCaseInsensitiveContains(searchText)
        }
        if filtered.isEmpty {
            ContentUnavailableView(
                directory.isEmpty ? "Keine anderen User" : "Nichts gefunden",
                systemImage: "person.3",
                description: Text(directory.isEmpty
                    ? "Aktuell bist du der einzige aktive Nutzer im System."
                    : "Kein Kollege passt zu \"\(searchText)\"."))
        } else {
            List {
                Section {
                    ForEach(filtered) { u in
                        HStack {
                            Image(systemName: "person.crop.circle.fill")
                                .foregroundStyle(Theme.tungstenBlue)
                                .frame(width: 24)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(u.name).font(.body.weight(.semibold))
                                Text(u.email).font(.caption.monospaced()).foregroundStyle(.secondary)
                            }
                            Spacer()
                            // "In meine Kontakte übernehmen" — spart Tippen
                            // wenn man den User später öfter braucht (bumpt
                            // LastUsedAt für Signatur-Autocomplete).
                            Button {
                                Task { await addToMy(u) }
                            } label: { Image(systemName: "person.crop.circle.badge.plus") }
                                .buttonStyle(.plain)
                                .foregroundStyle(Theme.tungstenBlue)
                        }
                        .padding(.vertical, 2)
                        // v1.10.82: App-Store-Blocker Apple 1.2 — jeder User
                        // muss meldbar und blockierbar sein.
                        .contextMenu {
                            Button(role: .destructive) {
                                pendingReportUser = (u.id, u.name)
                            } label: { Label("Melden…", systemImage: "flag") }
                            Button(role: .destructive) {
                                Task { await block(u.id, name: u.name) }
                            } label: { Label("Blockieren", systemImage: "hand.raised") }
                        }
                        .swipeActions {
                            Button {
                                pendingReportUser = (u.id, u.name)
                            } label: { Label("Melden", systemImage: "flag") }
                                .tint(.orange)
                            Button(role: .destructive) {
                                Task { await block(u.id, name: u.name) }
                            } label: { Label("Block", systemImage: "hand.raised") }
                        }
                    }
                } footer: {
                    Text("\(filtered.count) NimShare-User · nur lesend").font(.caption).foregroundStyle(.secondary)
                }
            }
            .searchable(text: $searchText, prompt: "Suchen")
        }
    }

    private func loadAll() async {
        loading = true; defer { loading = false }
        async let a = loadMyRaw()
        async let b = loadDirRaw()
        _ = await (a, b)
    }
    private func loadMy() async {
        loading = true; defer { loading = false }
        _ = await loadMyRaw()
    }
    private func loadMyRaw() async -> Void {
        guard let api = auth.api else { return }
        do { myContacts = try await api.listContacts(query: nil) }
        catch let ex { error = ex.localizedDescription }
    }
    private func loadDirRaw() async -> Void {
        guard let api = auth.api else { return }
        do { directory = try await api.listDirectoryUsers(query: nil) }
        catch ApiError.notFound {
            // v1.10.79: alter Server ohne /directory-Endpoint → leer lassen,
            // aber echte Fehler (5xx, Netzwerk) durchreichen damit User sieht
            // dass irgendwas kaputt ist.
            directory = []
        }
        catch let ex {
            error = ex.localizedDescription
        }
    }

    private func deleteContact(_ id: UUID) async {
        guard let api = auth.api else { return }
        do {
            try await api.deleteContact(id)
            await loadMy()
        } catch let ex { error = ex.localizedDescription }
    }

    /// v1.10.74: Directory-User als eigenen persönlichen Kontakt speichern.
    private func addToMy(_ u: DirectoryUserDto) async {
        guard let api = auth.api else { return }
        do {
            _ = try await api.createContact(email: u.email, name: u.name)
            mode = .mine
            await loadMy()
        } catch let ex { error = ex.localizedDescription }
    }
}

struct AddContactSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    // v1.10.113: optionaler Bestands-Kontakt → Bearbeiten-Modus.
    var existing: ContactDto? = nil
    let onSaved: () -> Void
    @State private var email = ""
    @State private var name = ""
    @State private var company = ""
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section("E-Mail") {
                    TextField("name@firma.tld", text: $email)
                        .keyboardType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                }
                Section("Name") {
                    TextField("Anzeigename", text: $name)
                }
                Section("Firma (optional)") {
                    TextField("Firma", text: $company)
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
            }
            .navigationTitle(existing == nil ? "Kontakt anlegen" : "Kontakt bearbeiten")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Speichern") { Task { await save() } }
                        .disabled(busy || !email.contains("@") || name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .onAppear {
                if let c = existing {
                    email = c.email; name = c.name; company = c.company ?? ""
                }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func save() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        let e = email.trimmingCharacters(in: .whitespaces)
        let n = name.trimmingCharacters(in: .whitespaces)
        let comp = company.isEmpty ? nil : company
        do {
            if let c = existing {
                _ = try await api.updateContact(id: c.id, email: e, name: n, company: comp)
            } else {
                _ = try await api.createContact(email: e, name: n, company: comp)
            }
            onSaved()
            dismiss()
        } catch let ex { error = ex.localizedDescription }
    }
}
