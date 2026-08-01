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
    // v1.11.50: Marcus's Wunsch — Suche, analog LinksView.
    @State private var searchQuery = ""

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, items.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.danger2)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                    Button("Erneut versuchen") { Task { await load() } }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                RSEmptyState(
                    systemImage: "tray.and.arrow.down",
                    title: "Keine Upload-Anforderungen",
                    desc: #"Erstelle eine Upload-Anforderung aus dem Kontext-Menü eines Ordners (Long-Press → „Upload anfordern“)."#)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                let q = searchQuery.trimmingCharacters(in: .whitespaces).lowercased()
                let visible = q.isEmpty ? items : items.filter { it in
                    it.slug.lowercased().contains(q) || (it.targetFolder?.lowercased().contains(q) ?? false)
                }
                List {
                    ForEach(visible) { row(for: $0) }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle("Upload-Anforderungen")
        .searchable(text: $searchQuery, prompt: "Slug, Zielordner")
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
                ZStack {
                    Circle().fill(Theme.navy.opacity(0.12)).frame(width: 32, height: 32)
                    Image(systemName: "tray.and.arrow.down.fill")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(Theme.navy)
                }
                Text(it.slug).font(TFont.titleS.monospaced()).foregroundStyle(Theme.textPrimary)
                Spacer()
                statusChip(it)
            }
            HStack(spacing: 12) {
                Label("\(it.uploadCount)\(it.maxUploads.map { "/\($0)" } ?? "")",
                      systemImage: "arrow.up.doc.fill")
                    .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                if let target = it.targetFolder, !target.isEmpty {
                    Label(target, systemImage: "folder")
                        .font(TFont.caption).foregroundStyle(Theme.textSecondary).lineLimit(1)
                }
                // v1.11.50: ♾️ statt Datum, wenn die Anfrage explizit dauerhaft ist.
                if it.isPermanent {
                    Label("Dauerhaft", systemImage: "infinity")
                        .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                } else if let exp = it.expiresAt {
                    Label(exp.formatted(date: .abbreviated, time: .omitted), systemImage: "calendar")
                        .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                }
            }
        }
        .padding(.vertical, 4)
        .listRowBackground(Theme.surface2)
        .listRowSeparator(.hidden)
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) { pendingDelete = it } label: {
                Label("Widerrufen", systemImage: "xmark.circle")
            }
            Button { copyUrl(it.slug) } label: {
                Label("Kopieren", systemImage: "doc.on.doc")
            }.tint(Theme.cyan)
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
            chip("Widerrufen", color: Theme.textTertiary)
        } else if expired {
            chip("Abgelaufen", color: Theme.yellow)
        } else if limit {
            chip("Limit erreicht", color: Theme.yellow)
        } else {
            chip("Aktiv", color: Theme.success2)
        }
    }
    private func chip(_ text: String, color: Color) -> some View {
        Text(text).font(TFont.caption.weight(.semibold))
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
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
    private func deleteItem(_ id: UUID) async {
        guard let api = auth.api else { return }
        do {
            try await api.deleteUploadRequest(id)
            items.removeAll { $0.id == id }
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
