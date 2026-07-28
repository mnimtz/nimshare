import SwiftUI

/// v1.11.39 — iOS-Parität für Key-Store (Kunden + Lizenzschlüssel-Verwaltung).
/// Siehe Web /keystore. Reveal zeigt den Klartext-Key in einem Alert (analog
/// dem prompt()-Verhalten im Web).
struct KeyStoreView: View {
    @EnvironmentObject var auth: AuthStore

    static let defaultKeyTypes = ["Evaluation-CLS", "Evaluation-OnPrem", "NFR-OnPrem", "NFR-CLS"]
    static func isClsType(_ type: String) -> Bool { type.range(of: "cls", options: .caseInsensitive) != nil }

    @State private var rows: [NimShareAPI.KeyStoreEntryDto] = []
    @State private var searchText = ""
    @State private var loading = true
    @State private var error: String?
    @State private var showAdd = false
    @State private var editEntry: NimShareAPI.KeyStoreEntryDto?
    @State private var revealedValue: String?
    @State private var revealError: String?

    var body: some View {
        Group {
            if loading && rows.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if rows.isEmpty {
                ContentUnavailableView(
                    "Noch kein Schlüssel",
                    systemImage: "key.fill",
                    description: Text("Lege Kunden mit ihrem Lizenzschlüssel an (+ oben rechts)."))
            } else {
                List {
                    ForEach(rows) { row in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(row.customerName).font(.body.weight(.semibold))
                                Spacer()
                                statusChip(row)
                            }
                            Text(row.customerEmail ?? row.customerEmailDomain.map { "@\($0)" } ?? "—")
                                .font(.caption.monospaced()).foregroundStyle(.secondary)
                            HStack(spacing: 6) {
                                Text(row.keyType).font(.caption).foregroundStyle(Theme.tungstenBlue)
                                if let until = row.validUntil {
                                    Text("· bis \(until.formatted(date: .abbreviated, time: .omitted))")
                                        .font(.caption2).foregroundStyle(.secondary)
                                }
                                if !row.isOwnedByMe, let owner = row.ownerName {
                                    Text("· \(owner)").font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                        }
                        .padding(.vertical, 2)
                        .contentShape(Rectangle())
                        .contextMenu {
                            Button { Task { await reveal(row.id) } } label: { Label("Key anzeigen", systemImage: "eye") }
                            Button { editEntry = row } label: { Label("Bearbeiten", systemImage: "pencil") }
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                        }
                        .swipeActions(edge: .trailing) {
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                            Button { editEntry = row } label: { Label("Bearbeiten", systemImage: "pencil") }.tint(Theme.tungstenBlue)
                        }
                        .swipeActions(edge: .leading) {
                            Button { Task { await reveal(row.id) } } label: { Label("Key", systemImage: "eye") }.tint(.orange)
                        }
                    }
                }
                .searchable(text: $searchText, prompt: "Kunde, Email, Typ")
                .onChange(of: searchText) { _, _ in Task { await load() } }
            }
        }
        .navigationTitle("Key-Store")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                NavigationLink { KeyStoreDocumentsView() } label: { Image(systemName: "doc.text") }
            }
            ToolbarItem(placement: .topBarTrailing) {
                Button { showAdd = true } label: { Image(systemName: "plus") }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showAdd) {
            KeyStoreEntrySheet { Task { await load() } }
        }
        .sheet(item: $editEntry) { e in
            KeyStoreEntrySheet(existing: e) { Task { await load() } }
        }
        .alert("Lizenzschlüssel", isPresented: Binding(get: { revealedValue != nil }, set: { if !$0 { revealedValue = nil } })) {
            Button("Kopieren") { UIPasteboard.general.string = revealedValue }
            Button("Schließen", role: .cancel) { revealedValue = nil }
        } message: { Text(revealedValue ?? "") }
        .alert("Fehler", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        } message: { Text(error ?? "") }
        .alert("Fehler", isPresented: Binding(get: { revealError != nil }, set: { if !$0 { revealError = nil } })) {
            Button("OK") { revealError = nil }
        } message: { Text(revealError ?? "") }
    }

    @ViewBuilder
    private func statusChip(_ row: NimShareAPI.KeyStoreEntryDto) -> some View {
        let now = Date()
        if let until = row.validUntil, until < now {
            Text("Abgelaufen").font(.caption2).foregroundStyle(.white).padding(.horizontal, 6).padding(.vertical, 2).background(Theme.warnRed).clipShape(Capsule())
        } else if let from = row.validFrom, from > now {
            Text("Zukünftig").font(.caption2).foregroundStyle(.white).padding(.horizontal, 6).padding(.vertical, 2).background(.orange).clipShape(Capsule())
        } else {
            Text("Aktiv").font(.caption2).foregroundStyle(.white).padding(.horizontal, 6).padding(.vertical, 2).background(.green).clipShape(Capsule())
        }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { rows = try await api.listKeyStoreEntries(q: searchText.isEmpty ? nil : searchText) }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
    private func reveal(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { revealedValue = try await api.revealKeyStoreEntry(id).keyValue }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { revealError = ex.localizedDescription }
    }
    private func delete(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.deleteKeyStoreEntry(id); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}

struct KeyStoreEntrySheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    var existing: NimShareAPI.KeyStoreEntryDto? = nil
    let onSaved: () -> Void

    @State private var customerName = ""
    @State private var customerEmail = ""
    @State private var customerDomain = ""
    @State private var keyType = ""
    // v1.11.39: -CLS-Typen brauchen zwei Werte (Seriennummer + Produktcode),
    // andere Typen einen einzelnen Key-String — gleiche Logik wie im Web.
    @State private var keyValue = ""
    @State private var keySerial = ""
    @State private var keyProduct = ""
    @State private var validUntil: Date = .now
    @State private var hasValidUntil = false
    @State private var notes = ""
    @State private var busy = false
    @State private var error: String?

    private var isCls: Bool { KeyStoreView.isClsType(keyType) }

    var body: some View {
        NavigationStack {
            Form {
                Section("Kunde") {
                    TextField("Firma/Name", text: $customerName)
                }
                Section("Kontakt") {
                    TextField("kunde@firma.com", text: $customerEmail)
                        .keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                    TextField("oder Domain: firma.com", text: $customerDomain)
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                Section("Key-Typ") {
                    TextField("z.B. Evaluation-CLS", text: $keyType)
                        .textInputAutocapitalization(.never).autocorrectionDisabled()
                    Menu("Vorschläge") {
                        ForEach(KeyStoreView.defaultKeyTypes, id: \.self) { t in
                            Button(t) { keyType = t }
                        }
                    }
                }
                if isCls {
                    Section("Key-Wert") {
                        TextField(existing == nil ? "Seriennummer (z.B. LW13976)" : "Seriennummer (unverändert lassen = leer)", text: $keySerial)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                        TextField(existing == nil ? "Produktcode (z.B. 729093F57)" : "Produktcode (unverändert lassen = leer)", text: $keyProduct)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                } else {
                    Section("Key-Wert") {
                        TextField(existing == nil ? "AV09Z-K13-8FXD-BAVZ-XT" : "Unverändert lassen = leer", text: $keyValue)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                }
                Section("Gültig bis (optional)") {
                    Toggle("Ablaufdatum setzen", isOn: $hasValidUntil)
                    if hasValidUntil {
                        DatePicker("Datum", selection: $validUntil, displayedComponents: .date)
                    }
                }
                Section("Notizen") {
                    TextField("Optional", text: $notes, axis: .vertical)
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
            }
            .navigationTitle(existing == nil ? "Neuer Schlüssel" : "Schlüssel bearbeiten")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Speichern") { Task { await save() } }
                        .disabled(busy || customerName.trimmingCharacters(in: .whitespaces).isEmpty
                            || (customerEmail.isEmpty && customerDomain.isEmpty) || keyType.isEmpty)
                }
            }
            .task(id: existing?.id) {
                if let e = existing {
                    customerName = e.customerName
                    customerEmail = e.customerEmail ?? ""
                    customerDomain = e.customerEmailDomain ?? ""
                    keyType = e.keyType
                    notes = e.notes ?? ""
                    if let until = e.validUntil { validUntil = until; hasValidUntil = true }
                }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func save() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        // v1.11.39: gleiche Kombinationslogik wie ksSave() im Web — kein
        // eigenes Server-Feld für die zwei CLS-Teile, sie werden zu einem
        // String "Seriennummer / Produktcode" zusammengefasst.
        var combinedValue = ""
        if isCls {
            let s = keySerial.trimmingCharacters(in: .whitespaces)
            let p = keyProduct.trimmingCharacters(in: .whitespaces)
            if !s.isEmpty && !p.isEmpty { combinedValue = "\(s) / \(p)" }
            else if !s.isEmpty || !p.isEmpty {
                error = "Bitte beide Teile (Seriennummer + Produktcode) ausfüllen."
                return
            }
        } else {
            combinedValue = keyValue.trimmingCharacters(in: .whitespaces)
        }
        if existing == nil && combinedValue.isEmpty {
            error = "Key-Wert ist erforderlich."
            return
        }
        do {
            if let e = existing {
                let body = NimShareAPI.UpdateKeyStoreEntryBody(
                    customerName: customerName, customerEmail: customerEmail.isEmpty ? nil : customerEmail,
                    customerEmailDomain: customerDomain.isEmpty ? nil : customerDomain, keyType: keyType,
                    keyValue: combinedValue.isEmpty ? nil : combinedValue,
                    validFrom: nil, validUntil: hasValidUntil ? validUntil : nil, notes: notes.isEmpty ? nil : notes,
                    clearValidFrom: false, clearValidUntil: !hasValidUntil)
                _ = try await api.updateKeyStoreEntry(e.id, body)
            } else {
                let body = NimShareAPI.CreateKeyStoreEntryBody(
                    customerName: customerName, customerEmail: customerEmail.isEmpty ? nil : customerEmail,
                    customerEmailDomain: customerDomain.isEmpty ? nil : customerDomain, keyType: keyType,
                    keyValue: combinedValue, validFrom: nil, validUntil: hasValidUntil ? validUntil : nil,
                    notes: notes.isEmpty ? nil : notes)
                _ = try await api.createKeyStoreEntry(body)
            }
            onSaved()
            dismiss()
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
