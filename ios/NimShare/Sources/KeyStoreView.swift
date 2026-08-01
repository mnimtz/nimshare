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
    @State private var searchTask: Task<Void, Never>?

    var body: some View {
        Group {
            if loading && rows.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if rows.isEmpty {
                RSEmptyState(
                    systemImage: "key.fill",
                    title: "Noch kein Schlüssel",
                    desc: "Lege Kunden mit ihrem Lizenzschlüssel an (+ oben rechts).")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(rows) { row in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(row.customerName).font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                                Spacer()
                                statusChip(row)
                            }
                            Text(row.customerEmail ?? row.customerEmailDomain.map { "@\($0)" } ?? "—")
                                .font(.caption.monospaced()).foregroundStyle(Theme.textSecondary)
                            HStack(spacing: 6) {
                                Text(row.keyType).font(TFont.caption).foregroundStyle(Theme.cyan)
                                if let until = row.validUntil {
                                    Text("· bis \(until.formatted(date: .abbreviated, time: .omitted))")
                                        .font(TFont.caption).foregroundStyle(Theme.textTertiary)
                                }
                                if !row.isOwnedByMe, let owner = row.ownerName {
                                    Text("· \(owner)").font(TFont.caption).foregroundStyle(Theme.textTertiary)
                                }
                            }
                        }
                        .padding(.vertical, 4)
                        .contentShape(Rectangle())
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                        .contextMenu {
                            Button { Task { await reveal(row.id) } } label: { Label("Key anzeigen", systemImage: "eye") }
                            Button { editEntry = row } label: { Label("Bearbeiten", systemImage: "pencil") }
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                        }
                        .swipeActions(edge: .trailing) {
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                            Button { editEntry = row } label: { Label("Bearbeiten", systemImage: "pencil") }.tint(Theme.cyan)
                        }
                        .swipeActions(edge: .leading) {
                            Button { Task { await reveal(row.id) } } label: { Label("Key", systemImage: "eye") }.tint(Theme.yellow)
                        }
                    }
                }
                .scrollContentBackground(.hidden)
                .searchable(text: $searchText, prompt: "Kunde, Email, Typ")
                // v1.11.55: undebounced Suche konnte bei Netz-Jitter Ergebnisse
                // eines älteren, kürzeren Suchbegriffs über die des aktuellen
                // schreiben (Out-of-Order-Response) — jetzt wie PermissionsSheet
                // debounced + der vorherige In-Flight-Task wird gecancelt.
                .onChange(of: searchText) { _, _ in
                    searchTask?.cancel()
                    searchTask = Task {
                        try? await Task.sleep(nanoseconds: 250_000_000)
                        if Task.isCancelled { return }
                        await load()
                    }
                }
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle("Kunden")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                NavigationLink { KeyStoreLicensesView() } label: { Image(systemName: "tag") }
            }
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
            Chip(text: "Abgelaufen", color: Theme.danger2, bg: Theme.danger2.opacity(0.12))
        } else if let from = row.validFrom, from > now {
            Chip(text: "Zukünftig", color: Theme.yellow, bg: Theme.yellow.opacity(0.12))
        } else {
            Chip(text: "Aktiv", color: Theme.success2, bg: Theme.success2.opacity(0.12))
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

    // v1.11.56: bei Neuanlage kann statt eines neu getippten Keys eine
    // vorrätige Lizenz zugewiesen werden ("Lizenzen"-Tab).
    private enum KeyMode { case manual, pool }
    @State private var keyMode: KeyMode = .manual
    @State private var poolEntries: [NimShareAPI.KeyStorePoolEntryDto] = []
    @State private var selectedPoolId: UUID?
    @State private var poolLoading = false

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
                if existing == nil {
                    Section {
                        Picker("", selection: $keyMode) {
                            Text("Neu eingeben").tag(KeyMode.manual)
                            Text("Aus Vorrat wählen").tag(KeyMode.pool)
                        }
                        .pickerStyle(.segmented)
                    }
                }
                if keyMode == .pool {
                    Section("Lizenz aus Vorrat") {
                        if poolLoading {
                            ProgressView()
                        } else if poolEntries.isEmpty {
                            Text("— keine im Vorrat für diesen Typ —").foregroundStyle(.secondary)
                        } else {
                            Picker("Lizenz", selection: $selectedPoolId) {
                                Text("—").tag(UUID?.none)
                                ForEach(poolEntries) { p in
                                    Text(poolEntryLabel(p)).tag(Optional(p.id))
                                }
                            }
                        }
                        NavigationLink("Vorrat verwalten") { KeyStoreLicensesView() }
                            .font(.footnote)
                    }
                } else if isCls {
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
                if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.bgGradient.ignoresSafeArea())
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
            .onChange(of: keyMode) { _, mode in if mode == .pool { Task { await loadPool() } } }
            .onChange(of: keyType) { _, _ in if keyMode == .pool { Task { await loadPool() } } }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func poolEntryLabel(_ p: NimShareAPI.KeyStorePoolEntryDto) -> String {
        var parts = [p.createdAt.formatted(date: .abbreviated, time: .omitted)]
        if p.isGlobal { parts.append("Global") }
        if let owner = p.ownerName { parts.append(owner) }
        if let notes = p.notes, !notes.isEmpty { parts.append(notes) }
        return parts.joined(separator: " · ")
    }

    private func loadPool() async {
        guard let api = auth.api else { return }
        poolLoading = true; defer { poolLoading = false }
        selectedPoolId = nil
        poolEntries = (try? await api.listKeyStorePool(type: keyType.isEmpty ? nil : keyType)) ?? []
    }

    private func save() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }

        // v1.11.56: aus dem Lizenzen-Vorrat zuweisen statt neu eintippen.
        if existing == nil && keyMode == .pool {
            guard let poolId = selectedPoolId else {
                error = "Bitte eine Lizenz aus dem Vorrat auswählen."
                return
            }
            do {
                var body = NimShareAPI.CreateKeyStoreEntryBody(
                    customerName: customerName, customerEmail: customerEmail.isEmpty ? nil : customerEmail,
                    customerEmailDomain: customerDomain.isEmpty ? nil : customerDomain, keyType: "",
                    keyValue: "", validFrom: nil, validUntil: hasValidUntil ? validUntil : nil,
                    notes: notes.isEmpty ? nil : notes)
                body.poolEntryId = poolId
                _ = try await api.createKeyStoreEntry(body)
                onSaved()
                dismiss()
            } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
            return
        }

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
