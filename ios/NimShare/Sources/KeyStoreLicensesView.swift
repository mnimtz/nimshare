import SwiftUI

/// v1.11.56 — iOS-Parität für den Key-Store-"Lizenzen"-Vorrat (siehe Web
/// /keystore/licenses). Schlüssel können hier ohne Kunde eingelagert und
/// später im Neuer-Kunde-Dialog (KeyStoreEntrySheet) zugewiesen werden.
struct KeyStoreLicensesView: View {
    @EnvironmentObject var auth: AuthStore

    @State private var rows: [NimShareAPI.KeyStoreEntryDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var showAdd = false
    @State private var revealedValue: String?
    @State private var revealError: String?
    @State private var pendingReset: NimShareAPI.KeyStoreEntryDto?

    var body: some View {
        Group {
            if loading && rows.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if rows.isEmpty {
                RSEmptyState(
                    systemImage: "tag",
                    title: "Noch keine Lizenzen im Vorrat",
                    desc: "Lege Lizenzschlüssel ohne Kunde an (+ oben rechts), um sie später zuzuweisen.")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(rows) { row in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(row.keyType).font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                                Spacer()
                                statusChip(row)
                            }
                            HStack(spacing: 6) {
                                Text(row.createdAt.formatted(date: .abbreviated, time: .omitted))
                                    .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                                if row.isGlobal {
                                    Text("· Global").font(TFont.caption).foregroundStyle(Theme.cyan)
                                }
                                if !row.isOwnedByMe, let owner = row.ownerName {
                                    Text("· \(owner)").font(TFont.caption).foregroundStyle(Theme.textTertiary)
                                }
                            }
                            if row.status == .assigned {
                                Text(row.customerName).font(TFont.caption).foregroundStyle(Theme.textTertiary)
                            }
                        }
                        .padding(.vertical, 4)
                        .contentShape(Rectangle())
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                        .contextMenu {
                            Button { Task { await reveal(row.id) } } label: { Label("Key anzeigen", systemImage: "eye") }
                            if row.status == .assigned {
                                Button { pendingReset = row } label: { Label("Zurücksetzen", systemImage: "arrow.uturn.backward") }
                            }
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                        }
                        .swipeActions(edge: .trailing) {
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                            if row.status == .assigned {
                                Button { pendingReset = row } label: { Label("Reset", systemImage: "arrow.uturn.backward") }.tint(Theme.yellow)
                            }
                        }
                        .swipeActions(edge: .leading) {
                            Button { Task { await reveal(row.id) } } label: { Label("Key", systemImage: "eye") }.tint(Theme.cyan)
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle("Lizenzen")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showAdd = true } label: { Image(systemName: "plus") }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showAdd) {
            KeyStoreLicenseAddSheet { Task { await load() } }
        }
        .alert("Lizenzschlüssel", isPresented: Binding(get: { revealedValue != nil }, set: { if !$0 { revealedValue = nil } })) {
            Button("Kopieren") {
                // v1.11.82 (Security-Review): Lizenzschlüssel ist ein Secret — lokal-only
                // in die Zwischenablage und nach 60 s automatisch löschen.
                if let v = revealedValue {
                    UIPasteboard.general.setItems(
                        [["public.utf8-plain-text": v]],
                        options: [.localOnly: true,
                                  .expirationDate: Date().addingTimeInterval(60)])
                }
            }
            Button("Schließen", role: .cancel) { revealedValue = nil }
        } message: { Text(revealedValue ?? "") }
        .alert("Fehler", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        } message: { Text(error ?? "") }
        .alert("Fehler", isPresented: Binding(get: { revealError != nil }, set: { if !$0 { revealError = nil } })) {
            Button("OK") { revealError = nil }
        } message: { Text(revealError ?? "") }
        .alert("Zuordnung aufheben?", isPresented: Binding(get: { pendingReset != nil }, set: { if !$0 { pendingReset = nil } })) {
            Button("Abbrechen", role: .cancel) { pendingReset = nil }
            Button("Zurücksetzen", role: .destructive) {
                if let r = pendingReset { Task { await reset(r.id) } }
                pendingReset = nil
            }
        } message: {
            Text(pendingReset.map { "Zuordnung zu \"\($0.customerName)\" aufheben und Lizenz wieder verfügbar machen?" } ?? "")
        }
    }

    @ViewBuilder
    private func statusChip(_ row: NimShareAPI.KeyStoreEntryDto) -> some View {
        if row.status == .available {
            Chip(text: "Verfügbar", color: Theme.success2, bg: Theme.success2.opacity(0.12))
        } else {
            Chip(text: "Verbraucht", color: Theme.yellow, bg: Theme.yellow.opacity(0.12))
        }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { rows = try await api.listKeyStoreLicenses() }
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
    private func reset(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { _ = try await api.resetKeyStoreEntry(id); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}

struct KeyStoreLicenseAddSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let onSaved: () -> Void

    @State private var keyType = ""
    @State private var keyValue = ""
    @State private var keySerial = ""
    @State private var keyProduct = ""
    @State private var notes = ""
    @State private var isGlobal = false
    @State private var busy = false
    @State private var error: String?

    private var isAdmin: Bool { auth.user?.role == "Admin" }
    private var isCls: Bool { KeyStoreView.isClsType(keyType) }

    var body: some View {
        NavigationStack {
            Form {
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
                        TextField("Seriennummer (z.B. LW13976)", text: $keySerial)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                        TextField("Produktcode (z.B. 729093F57)", text: $keyProduct)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                } else {
                    Section("Key-Wert") {
                        TextField("AV09Z-K13-8FXD-BAVZ-XT", text: $keyValue)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                }
                Section("Notizen") {
                    TextField("Optional", text: $notes, axis: .vertical)
                }
                if isAdmin {
                    Section {
                        Toggle("Für alle Nutzer freigeben", isOn: $isGlobal)
                    } footer: {
                        Text("Macht diese Lizenz für jeden Nutzer entnehmbar, nicht nur für dich.")
                    }
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.bgGradient.ignoresSafeArea())
            .navigationTitle("Neue Lizenz")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Speichern") { Task { await save() } }
                        .disabled(busy || keyType.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func save() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
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
        if combinedValue.isEmpty {
            error = "Key-Wert ist erforderlich."
            return
        }
        do {
            let body = NimShareAPI.CreateKeyStoreLicenseBody(
                keyType: keyType.trimmingCharacters(in: .whitespaces), keyValue: combinedValue,
                notes: notes.isEmpty ? nil : notes, isGlobal: isAdmin && isGlobal)
            _ = try await api.createKeyStoreLicense(body)
            onSaved()
            dismiss()
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
