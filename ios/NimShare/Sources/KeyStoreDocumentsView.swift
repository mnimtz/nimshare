import SwiftUI
import UniformTypeIdentifiers

/// v1.11.39 — iOS-Parität für Key-Store-Dokumentation (siehe Web /keystore,
/// Sektion "Dokumentation", v1.11.37). PDFs oder feste Links, gebunden an
/// eine Auswahl von Key-Typen — erscheinen auf der Landing eines
/// KeyStoreMode-Links, sobald dort die "Dokumentation"-Checkbox aktiv ist.
struct KeyStoreDocumentsView: View {
    @EnvironmentObject var auth: AuthStore

    @State private var rows: [NimShareAPI.KeyStoreDocumentDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var showAdd = false
    @State private var editDoc: NimShareAPI.KeyStoreDocumentDto?

    var body: some View {
        Group {
            if loading && rows.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if rows.isEmpty {
                RSEmptyState(
                    systemImage: "doc.text",
                    title: "Noch kein Dokument",
                    desc: "PDFs oder feste Links (z.B. Tenant-Login), gebunden an Key-Typen (+ oben rechts).")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(rows) { row in
                        HStack(spacing: 12) {
                            ZStack {
                                Circle().fill(Theme.navy.opacity(0.12)).frame(width: 34, height: 34)
                                Image(systemName: row.isFile ? "doc.fill" : "link")
                                    .font(.system(size: 14, weight: .semibold))
                                    .foregroundStyle(Theme.navyFg)
                            }
                            VStack(alignment: .leading, spacing: 2) {
                                Text(row.label).font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                                Text(row.keyTypes.joined(separator: ", "))
                                    .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                            }
                        }
                        .padding(.vertical, 4)
                        .contentShape(Rectangle())
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                        .contextMenu {
                            Button { editDoc = row } label: { Label("Bearbeiten", systemImage: "pencil") }
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                        }
                        .swipeActions(edge: .trailing) {
                            Button(role: .destructive) { Task { await delete(row.id) } } label: { Label("Löschen", systemImage: "trash") }
                            Button { editDoc = row } label: { Label("Bearbeiten", systemImage: "pencil") }.tint(Theme.cyan)
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle("Dokumentation")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showAdd = true } label: { Image(systemName: "plus") }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showAdd) {
            KeyStoreDocumentSheet { Task { await load() } }
        }
        .sheet(item: $editDoc) { d in
            KeyStoreDocumentSheet(existing: d) { Task { await load() } }
        }
        .alert("Fehler", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        } message: { Text(error ?? "") }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { rows = try await api.listKeyStoreDocuments() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
    private func delete(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.deleteKeyStoreDocument(id); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}

struct KeyStoreDocumentSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    var existing: NimShareAPI.KeyStoreDocumentDto? = nil
    let onSaved: () -> Void

    @State private var label = ""
    @State private var isFile = true
    @State private var url = ""
    @State private var selectedTypes: Set<String> = []
    @State private var customTypes: [String] = []
    @State private var pickedFileData: Data?
    @State private var pickedFileName: String?
    @State private var showPicker = false
    @State private var busy = false
    @State private var error: String?

    private var allTypes: [String] { KeyStoreView.defaultKeyTypes + customTypes.filter { !KeyStoreView.defaultKeyTypes.contains($0) } }

    var body: some View {
        NavigationStack {
            Form {
                Section("Beschriftung") {
                    TextField("Button-Text, z.B. \"Tenant\"", text: $label)
                }
                if existing == nil {
                    Section("Art") {
                        Picker("Art", selection: $isFile) {
                            Text("Datei (PDF)").tag(true)
                            Text("Fester Link").tag(false)
                        }
                        .pickerStyle(.segmented)
                    }
                }
                if isFile {
                    Section("Datei") {
                        if let name = pickedFileName {
                            Label(name, systemImage: "doc.fill")
                        } else if let fn = existing?.fileName {
                            Text("Aktuell: \(fn)").foregroundStyle(.secondary)
                        }
                        Button(existing == nil ? "PDF wählen…" : "PDF ersetzen…") { showPicker = true }
                    }
                } else {
                    Section("URL") {
                        TextField("https://admin.ppdf.kofaxcloud.com/", text: $url)
                            .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                }
                Section("Gilt für Key-Typen") {
                    ForEach(allTypes, id: \.self) { t in
                        Button {
                            if selectedTypes.contains(t) { selectedTypes.remove(t) } else { selectedTypes.insert(t) }
                        } label: {
                            HStack {
                                Text(t)
                                Spacer()
                                if selectedTypes.contains(t) { Image(systemName: "checkmark").foregroundStyle(Theme.navyFg) }
                            }
                        }
                        .foregroundStyle(.primary)
                    }
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.bgGradient.ignoresSafeArea())
            .navigationTitle(existing == nil ? "Neues Dokument" : "Dokument bearbeiten")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Speichern") { Task { await save() } }
                        .disabled(busy || label.trimmingCharacters(in: .whitespaces).isEmpty || selectedTypes.isEmpty
                            || (!isFile && url.isEmpty) || (existing == nil && isFile && pickedFileData == nil))
                }
            }
            .task(id: existing?.id) {
                if let d = existing {
                    label = d.label
                    isFile = d.isFile
                    url = d.url ?? ""
                    selectedTypes = Set(d.keyTypes)
                    customTypes = d.keyTypes.filter { !KeyStoreView.defaultKeyTypes.contains($0) }
                }
            }
            .overlay { if busy { ProgressView() } }
            .fileImporter(isPresented: $showPicker, allowedContentTypes: [.pdf], allowsMultipleSelection: false) { result in
                Task { await handlePicked(result) }
            }
        }
    }

    private func handlePicked(_ result: Result<[URL], Error>) async {
        do {
            let urls = try result.get()
            guard let src = urls.first else { return }
            let didStart = src.startAccessingSecurityScopedResource()
            defer { if didStart { src.stopAccessingSecurityScopedResource() } }
            pickedFileData = try Data(contentsOf: src)
            pickedFileName = src.lastPathComponent
        } catch is CancellationError { /* User hat abgebrochen */ } catch let ex { error = ex.localizedDescription }
    }

    private func save() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        let types = Array(selectedTypes)
        do {
            if let d = existing {
                if let data = pickedFileData, let name = pickedFileName {
                    _ = try await api.replaceKeyStoreDocumentFile(d.id, fileData: data, fileName: name, mimeType: "application/pdf")
                }
                _ = try await api.updateKeyStoreDocument(d.id, label: label, url: d.isFile ? nil : url, keyTypes: types)
            } else if isFile, let data = pickedFileData, let name = pickedFileName {
                _ = try await api.uploadKeyStoreDocument(label: label, keyTypes: types, fileData: data, fileName: name, mimeType: "application/pdf")
            } else {
                _ = try await api.createKeyStoreLinkDocument(label: label, url: url, keyTypes: types)
            }
            onSaved()
            dismiss()
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
