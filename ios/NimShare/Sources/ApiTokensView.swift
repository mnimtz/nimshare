import SwiftUI

/// v1.10.88: API-Tokens verwalten in iOS — Parität zum Web /settings/dev.
/// Create-Flow zeigt den Raw-Token nur EINMAL (danach nur Hash gespeichert).
/// Marcus's Regel aus Web: „Tokens können nur aus Cookie-Session erstellt
/// werden" gilt auch hier — funktioniert weil iOS mit JWT eingeloggt ist,
/// aber der Server prüft nicht nach dieser Herkunft, weil kein
/// nimshare.api_token-Claim im JWT sitzt.
struct ApiTokensView: View {
    @EnvironmentObject var auth: AuthStore

    @State private var tokens: [NimShareAPI.ApiTokenDto] = []
    @State private var loading = false
    @State private var error: String?
    @State private var showCreate = false
    @State private var justCreated: NimShareAPI.CreatedApiTokenDto?

    var body: some View {
        List {
            if tokens.isEmpty && !loading {
                ContentUnavailableView(
                    "Keine API-Tokens",
                    systemImage: "key",
                    description: Text("Erstelle einen Token um NimShare per API zu automatisieren."))
            }
            ForEach(tokens) { t in
                VStack(alignment: .leading, spacing: 2) {
                    HStack {
                        Text(t.name).font(.body.weight(.semibold))
                        Spacer()
                        if t.revokedAt != nil {
                            Text("widerrufen").font(.caption).foregroundStyle(.red)
                        } else if let exp = t.expiresAt, exp < Date() {
                            Text("abgelaufen").font(.caption).foregroundStyle(.orange)
                        }
                    }
                    Text(t.prefix + "…").font(.caption.monospaced()).foregroundStyle(.secondary)
                    HStack(spacing: 6) {
                        Text(t.createdAt, style: .date)
                        if let scopes = t.scopes, !scopes.isEmpty {
                            Text("· \(scopes)")
                        }
                        if let last = t.lastUsedAt {
                            Text("· zuletzt: \(last, style: .relative)")
                        }
                    }
                    .font(.caption).foregroundStyle(.secondary)
                }
                .swipeActions {
                    if t.revokedAt == nil {
                        Button(role: .destructive) {
                            Task { await revoke(t.id) }
                        } label: { Label("Widerrufen", systemImage: "xmark.circle") }
                    }
                }
            }
            if let e = error {
                Section { Text(e).foregroundStyle(Theme.warnRed).font(.footnote) }
            }
        }
        .navigationTitle("API-Tokens")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showCreate = true } label: { Image(systemName: "plus") }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showCreate) {
            CreateApiTokenSheet { created in
                justCreated = created
                Task { await load() }
            }
        }
        .sheet(item: $justCreated) { c in
            NavigationStack {
                Form {
                    Section {
                        Label("Nur JETZT sichtbar", systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(Theme.warnRed).font(.body.weight(.semibold))
                        Text("Speichere den Token jetzt an einem sicheren Ort. Er wird niemals wieder angezeigt.")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    Section("Token") {
                        Text(c.rawToken)
                            .font(.system(.footnote, design: .monospaced))
                            .textSelection(.enabled)
                    }
                    Section {
                        Button {
                            UIPasteboard.general.string = c.rawToken
                        } label: { Label("Kopieren", systemImage: "doc.on.doc") }
                    }
                }
                .navigationTitle(c.token.name)
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Fertig") { justCreated = nil }
                    }
                }
            }
        }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do { tokens = try await api.listApiTokens() }
        catch let ex { error = ex.localizedDescription }
    }
    private func revoke(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.revokeApiToken(id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
}

extension NimShareAPI.CreatedApiTokenDto: Identifiable {
    public var id: UUID { token.id }
}

private struct CreateApiTokenSheet: View {
    let onCreated: (NimShareAPI.CreatedApiTokenDto) -> Void
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var scopes = ""
    @State private var hasExpiry = false
    @State private var expiresAt = Date().addingTimeInterval(365 * 24 * 3600)
    @State private var submitting = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section("Name") {
                    TextField("z.B. CI-Deploy, Backup-Job", text: $name)
                        .autocorrectionDisabled()
                }
                Section {
                    TextField("Scopes (optional, z.B. files:read)", text: $scopes)
                        .autocorrectionDisabled()
                } footer: {
                    Text("Leer = voller Zugriff. Mehrere per Komma.").font(.caption)
                }
                Section {
                    Toggle("Ablaufdatum", isOn: $hasExpiry)
                    if hasExpiry {
                        DatePicker("Ablauf", selection: $expiresAt, displayedComponents: [.date])
                    }
                }
                if let e = error {
                    Section { Text(e).foregroundStyle(Theme.warnRed).font(.footnote) }
                }
            }
            .navigationTitle("Neuer Token")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Erstellen") { Task { await create() } }
                        .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || submitting)
                }
            }
        }
    }
    private func create() async {
        guard let api = auth.api else { return }
        submitting = true; error = nil; defer { submitting = false }
        do {
            let c = try await api.createApiToken(
                name: name.trimmingCharacters(in: .whitespaces),
                scopes: scopes.isEmpty ? nil : scopes,
                expiresAt: hasExpiry ? expiresAt : nil)
            onCreated(c)
            dismiss()
        } catch let ex { error = ex.localizedDescription }
    }
}
