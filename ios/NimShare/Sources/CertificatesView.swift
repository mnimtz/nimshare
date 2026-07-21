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
                ContentUnavailableView("Kein Zertifikat",
                    systemImage: "seal",
                    description: Text("Ohne Zertifikat wird beim Signieren ein Web-Only-Stempel genutzt. Für PKCS-signierte PDFs generiere ein selbst-signiertes Zertifikat."))
                    .overlay(alignment: .bottom) {
                        Button {
                            showGenerate = true
                        } label: {
                            Label("Zertifikat generieren", systemImage: "plus.circle.fill")
                        }
                        .buttonStyle(.borderedProminent).tint(Theme.tungstenBlue)
                        .padding(.bottom, 40)
                    }
            } else {
                List {
                    ForEach(items) { c in
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Image(systemName: c.isDefault ? "seal.fill" : "seal")
                                    .foregroundStyle(c.isDefault ? .green : Theme.tungstenBlue)
                                Text(c.name).font(.body.weight(.semibold))
                                Spacer()
                                if c.isExpired {
                                    Text("Abgelaufen")
                                        .font(.caption2.weight(.medium))
                                        .padding(.horizontal, 6).padding(.vertical, 2)
                                        .background(Theme.warnRed.opacity(0.15))
                                        .foregroundStyle(Theme.warnRed)
                                        .clipShape(RoundedRectangle(cornerRadius: 3))
                                } else if c.isDefault {
                                    Text("Standard")
                                        .font(.caption2.weight(.medium))
                                        .padding(.horizontal, 6).padding(.vertical, 2)
                                        .background(Color.green.opacity(0.15))
                                        .foregroundStyle(.green)
                                        .clipShape(RoundedRectangle(cornerRadius: 3))
                                }
                            }
                            Text("CN: \(c.subjectCommonName)").font(.caption).foregroundStyle(.secondary)
                            Text("Gültig bis \(c.notAfter.formatted(date: .abbreviated, time: .omitted))")
                                .font(.caption).foregroundStyle(.secondary)
                            Text("Fingerprint: \(c.thumbprint.prefix(16))…").font(.caption2.monospaced()).foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 4)
                        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                            if !c.isDefault {
                                Button {
                                    Task { await setDefault(c.id) }
                                } label: { Label("Standard", systemImage: "star.fill") }
                                    .tint(.yellow)
                            }
                            Button(role: .destructive) {
                                Task { await delete(c.id) }
                            } label: { Label("Löschen", systemImage: "trash") }
                        }
                    }
                }
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
        catch let ex { error = ex.localizedDescription }
    }
    private func setDefault(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.setDefaultCertificate(id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
    private func delete(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.deleteCertificate(id); await load() }
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
        } catch let ex { error = ex.localizedDescription }
    }
}
