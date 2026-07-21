import SwiftUI

/// v1.10.88: Webhooks verwalten in iOS. Parität zum Web /settings/dev.
struct WebhooksView: View {
    @EnvironmentObject var auth: AuthStore

    @State private var hooks: [NimShareAPI.WebhookDto] = []
    @State private var loading = false
    @State private var error: String?
    @State private var showCreate = false

    var body: some View {
        List {
            if hooks.isEmpty && !loading {
                ContentUnavailableView(
                    "Keine Webhooks",
                    systemImage: "bolt.horizontal",
                    description: Text("Konfiguriere Webhooks um bei Events (Upload, Signatur, Share) HTTP-Callbacks zu erhalten."))
            }
            ForEach(hooks) { w in
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Image(systemName: w.isActive ? "circle.fill" : "circle")
                            .foregroundStyle(w.isActive ? .green : .secondary)
                            .font(.caption)
                        Text(w.url).font(.footnote.monospaced()).lineLimit(1)
                    }
                    if let ev = w.events, !ev.isEmpty {
                        Text("Events: \(ev)").font(.caption2).foregroundStyle(.secondary)
                    }
                    HStack(spacing: 6) {
                        Text(w.createdAt, style: .date).font(.caption2).foregroundStyle(.secondary)
                        if w.failureCount > 0 {
                            Text("· \(w.failureCount) Fehler").font(.caption2).foregroundStyle(.red)
                        }
                        if let last = w.lastDeliveredAt {
                            Text("· zuletzt: \(last, style: .relative)").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                }
                .swipeActions {
                    Button(role: .destructive) {
                        Task { await remove(w.id) }
                    } label: { Label("Löschen", systemImage: "trash") }
                }
            }
            if let e = error {
                Section { Text(e).foregroundStyle(Theme.warnRed).font(.footnote) }
            }
        }
        .navigationTitle("Webhooks")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showCreate = true } label: { Image(systemName: "plus") }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showCreate) {
            CreateWebhookSheet { Task { await load() } }
        }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do { hooks = try await api.listWebhooks() }
        catch let ex { error = ex.localizedDescription }
    }
    private func remove(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.deleteWebhook(id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
}

private struct CreateWebhookSheet: View {
    let onCreated: () -> Void
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    @State private var url = ""
    @State private var secret = ""
    @State private var events = ""
    @State private var submitting = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section("Callback-URL") {
                    TextField("https://example.com/hook", text: $url)
                        .textContentType(.URL)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                }
                Section("Signatur-Secret") {
                    SecureField("Zufälliger geheimer String (HMAC)", text: $secret)
                        .textContentType(.newPassword)
                }
                Section("Events (optional)") {
                    TextField("z.B. file.uploaded, signature.completed", text: $events)
                        .autocorrectionDisabled()
                }
                if let e = error {
                    Section { Text(e).foregroundStyle(Theme.warnRed).font(.footnote) }
                }
            }
            .navigationTitle("Neuer Webhook")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Anlegen") { Task { await create() } }
                        .disabled(submitting || url.isEmpty || secret.isEmpty)
                }
            }
        }
    }
    private func create() async {
        guard let api = auth.api else { return }
        submitting = true; error = nil; defer { submitting = false }
        do {
            _ = try await api.createWebhook(url: url, secret: secret,
                events: events.isEmpty ? nil : events)
            onCreated()
            dismiss()
        } catch let ex { error = ex.localizedDescription }
    }
}
