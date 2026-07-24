import SwiftUI

/// v1.10.147 — Anzeige + Widerruf für Upload-Anforderungen (Reverse-Share-
/// Links). Der Server-Endpoint GET/DELETE /api/v1/upload-requests existierte
/// seit v1.7, iOS rief ihn nie — man erstellte eine URL, vergaß den Slug,
/// und kam nur noch übers Web dran. Jetzt: Liste analog LinksView, mit
/// Status-Chip (aktiv / abgelaufen / widerrufen / Limit erreicht),
/// Kopieren/Teilen und Widerrufen per Swipe/Context-Menü.
struct UploadRequestsView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [NimShareAPI.UploadRequestListItemDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var pendingDelete: NimShareAPI.UploadRequestListItemDto?

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, items.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                    Button("Erneut versuchen") { Task { await load() } }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView(
                    "Keine Upload-Anforderungen",
                    systemImage: "tray.and.arrow.down",
                    description: Text(#"Erstelle eine Upload-Anforderung aus dem Kontext-Menü eines Ordners (Long-Press → „Upload anfordern“)."#))
            } else {
                List {
                    ForEach(items) { row(for: $0) }
                }
            }
        }
        .navigationTitle("Upload-Anforderungen")
        .task { await load() }
        .refreshable { await load() }
        .alert(item: $pendingDelete) { it in
            Alert(
                title: Text("Anforderung widerrufen?"),
                message: Text(#"„\#(it.slug)“ wird endgültig entfernt. Neue Uploads sind danach nicht mehr möglich."#),
                primaryButton: .destructive(Text("Widerrufen")) { Task { await deleteItem(it.id) } },
                secondaryButton: .cancel())
        }
    }

    @ViewBuilder
    private func row(for it: NimShareAPI.UploadRequestListItemDto) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Image(systemName: "tray.and.arrow.down.fill")
                    .foregroundStyle(Theme.tungstenBlue)
                Text(it.slug).font(.body.weight(.semibold)).monospaced()
                Spacer()
                statusChip(it)
            }
            HStack(spacing: 12) {
                Label("\(it.uploadCount)\(it.maxUploads.map { "/\($0)" } ?? "")",
                      systemImage: "arrow.up.doc.fill")
                    .font(.caption).foregroundStyle(.secondary)
                if let target = it.targetFolder, !target.isEmpty {
                    Label(target, systemImage: "folder")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                if let exp = it.expiresAt {
                    Label(exp.formatted(date: .abbreviated, time: .omitted), systemImage: "calendar")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .padding(.vertical, 4)
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) { pendingDelete = it } label: {
                Label("Widerrufen", systemImage: "xmark.circle")
            }
            Button { copyUrl(it.slug) } label: {
                Label("Kopieren", systemImage: "doc.on.doc")
            }.tint(Theme.tungstenBlue)
        }
        .contextMenu {
            Button { copyUrl(it.slug) } label: { Label("URL kopieren", systemImage: "doc.on.doc") }
            if let url = urlFor(it.slug) {
                ShareLink(item: url) { Label("Teilen", systemImage: "square.and.arrow.up") }
            }
            Button(role: .destructive) { pendingDelete = it } label: {
                Label("Widerrufen", systemImage: "xmark.circle")
            }
        }
    }

    @ViewBuilder
    private func statusChip(_ it: NimShareAPI.UploadRequestListItemDto) -> some View {
        let now = Date()
        let expired = (it.expiresAt.map { $0 <= now }) ?? false
        let limit = it.maxUploads.map { it.uploadCount >= $0 } ?? false
        if it.isRevoked {
            chip("Widerrufen", color: .gray)
        } else if expired {
            chip("Abgelaufen", color: .orange)
        } else if limit {
            chip("Limit erreicht", color: .orange)
        } else {
            chip("Aktiv", color: .green)
        }
    }
    private func chip(_ text: String, color: Color) -> some View {
        Text(text).font(.caption2.weight(.semibold))
            .padding(.horizontal, 8).padding(.vertical, 2)
            .background(color.opacity(0.15)).foregroundStyle(color)
            .clipShape(Capsule())
    }

    private func urlFor(_ slug: String) -> URL? {
        guard let base = auth.serverURL else { return nil }
        return base.appendingPathComponent("u").appendingPathComponent(slug)
    }
    private func copyUrl(_ slug: String) {
        if let u = urlFor(slug) { UIPasteboard.general.string = u.absoluteString }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { items = try await api.listUploadRequests() }
        catch let ex { error = ex.localizedDescription }
    }
    private func deleteItem(_ id: UUID) async {
        guard let api = auth.api else { return }
        do {
            try await api.deleteUploadRequest(id)
            items.removeAll { $0.id == id }
        } catch let ex { error = ex.localizedDescription }
    }
}
