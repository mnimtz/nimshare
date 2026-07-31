import SwiftUI

/// v1.10.71: Signatur-Zertifikate-Verwaltung. Web-Parity: List mit
/// Default-Marker, Generate-Sheet (self-signed), Set-Default, Delete.
/// PFX-Import bewusst weggelassen — Marcus's Sicherheits-Regel "kein
/// fremdes PFX speichern" gilt weiterhin.
struct CertificatesView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [CertDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var showGenerate = false

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                RSEmptyState(
                    systemImage: "seal",
                    title: "Kein Zertifikat",
                    desc: "Ohne Zertifikat wird beim Signieren ein Web-Only-Stempel genutzt. Für PKCS-signierte PDFs generiere ein selbst-signiertes Zertifikat.",
                    ctaLabel: "Zertifikat generieren"
                ) { showGenerate = true }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(items) { c in
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Image(systemName: c.isDefault ? "seal.fill" : "seal")
                                    .foregroundStyle(c.isDefault ? Theme.success2 : Theme.navy)
                                Text(c.name).font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                                Spacer()
                                if c.isExpired {
                                    Chip(text: "Abgelaufen", color: Theme.danger2, bg: Theme.danger2.opacity(0.12))
                                } else if c.isDefault {
                                    Chip(text: "Standard", color: Theme.success2, bg: Theme.success2.opacity(0.12))
                                }
                            }
                            Text("CN: \(c.subjectCommonName)").font(TFont.caption).foregroundStyle(Theme.textSecondary)
                            Text("Gültig bis \(c.notAfter.formatted(date: .abbreviated, time: .omitted))")
                                .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                            Text("Fingerprint: \(c.thumbprint.prefix(16))…").font(.caption2.monospaced()).foregroundStyle(Theme.textTertiary)
                        }
                        .padding(.vertical, 4)
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                            if !c.isDefault {
                                Button {
                                    Task { await setDefault(c.id) }
                                } label: { Label("Standard", systemImage: "star.fill") }
                                    .tint(Theme.yellow)
                            }
                            Button(role: .destructive) {
                                Task { await delete(c.id) }
                            } label: { Label("Löschen", systemImage: "trash") }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
                .background(Theme.bgGradient.ignoresSafeArea())
            }
        }
        .navigationTitle("Zertifikate")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showGenerate = true } label: { Image(systemName: "plus") }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showGenerate) {
            GenerateCertSheet { Task { await load() } }
        }
        .alert("Fehler", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        } message: { Text(error ?? "") }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { items = try await api.listCertificates() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
    private func setDefault(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.setDefaultCertificate(id); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
    private func delete(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.deleteCertificate(id); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}

struct GenerateCertSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let onSaved: () -> Void

    @State private var name = ""
    @State private var commonName = ""
    @State private var organization = ""
    @State private var country = "DE"
    @State private var validityYears = 3
    @State private var setAsDefault = true
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section("Name (interne Bezeichnung)") {
                    TextField("z.B. Marcus 2026", text: $name)
                }
                Section("Common Name (CN)") {
                    TextField("Voller Name der Person", text: $commonName)
                }
                Section("Organisation (optional)") {
                    TextField("z.B. Tungsten Automation", text: $organization)
                }
                Section("Land (2 Buchstaben)") {
                    TextField("DE, AT, CH, …", text: $country)
                        .textInputAutocapitalization(.characters)
                        .onChange(of: country) { _, new in
                            country = String(new.uppercased().prefix(2))
                        }
                }
                Section("Gültigkeit") {
                    Picker("Jahre", selection: $validityYears) {
                        ForEach([1, 2, 3, 5, 10], id: \.self) { Text("\($0) Jahre").tag($0) }
                    }
                }
                Section {
                    Toggle("Als Standard setzen", isOn: $setAsDefault)
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
            }
            .navigationTitle("Neues Zertifikat")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Generieren") { Task { await generate() } }
                        .disabled(busy || name.isEmpty || commonName.isEmpty)
                }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func generate() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            _ = try await api.generateCertificate(
                name: name.trimmingCharacters(in: .whitespaces),
                commonName: commonName.trimmingCharacters(in: .whitespaces),
                organization: organization.isEmpty ? nil : organization,
                country: country.isEmpty ? nil : country,
                validityYears: validityYears,
                setAsDefault: setAsDefault
            )
            onSaved()
            dismiss()
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
