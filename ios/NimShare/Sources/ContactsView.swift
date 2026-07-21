import SwiftUI

/// v1.10.71: Adressbuch (Web-Parity). Liste mit Suche, Add/Delete,
/// zuletzt-verwendet-First.
struct ContactsView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [ContactDto] = []
    @State private var searchText = ""
    @State private var loading = true
    @State private var error: String?
    @State private var showAdd = false

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView("Kein Kontakt",
                    systemImage: "person.crop.circle",
                    description: Text("Kontakte tauchen automatisch auf wenn du jemanden zum Signieren einlädst — du kannst auch manuell welche anlegen."))
            } else {
                List {
                    ForEach(items) { c in
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
                        .swipeActions(edge: .trailing) {
                            Button(role: .destructive) {
                                Task { await deleteContact(c.id) }
                            } label: { Label("Löschen", systemImage: "trash") }
                        }
                    }
                }
                .searchable(text: $searchText, prompt: "Suchen")
                .onChange(of: searchText) { _, _ in Task { await load() } }
            }
        }
        .navigationTitle("Adressbuch")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showAdd = true } label: { Image(systemName: "plus") }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showAdd) {
            AddContactSheet { Task { await load() } }
        }
        .alert("Fehler", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        } message: { Text(error ?? "") }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true
        defer { loading = false }
        do { items = try await api.listContacts(query: searchText.isEmpty ? nil : searchText) }
        catch let ex { error = ex.localizedDescription }
    }

    private func deleteContact(_ id: UUID) async {
        guard let api = auth.api else { return }
        do {
            try await api.deleteContact(id)
            await load()
        } catch let ex { error = ex.localizedDescription }
    }
}

struct AddContactSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
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
            .navigationTitle("Kontakt anlegen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Speichern") { Task { await save() } }
                        .disabled(busy || !email.contains("@") || name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func save() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            _ = try await api.createContact(email: email.trimmingCharacters(in: .whitespaces),
                name: name.trimmingCharacters(in: .whitespaces),
                company: company.isEmpty ? nil : company)
            onSaved()
            dismiss()
        } catch let ex { error = ex.localizedDescription }
    }
}
