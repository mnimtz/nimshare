import SwiftUI

/// v1.10.71 / v1.10.148: 1:1-Parity mit Web-`/settings/links`.
/// Zeigt zwei Sektionen (👤 Privat / 🌍 Öffentlich, sofern befüllt) —
/// eine Gruppen-Sektion war ursprünglich als dritte geplant, ist aber
/// nicht implementiert: ShareLinkDto trägt kein Group-Kennzeichen,
/// Gruppen-Scope-Links fallen unter „Privat". Jede Row mit „📄 Datei: X"
/// oder „📁 Ordner: Y" statt bloß Slug, plus Status-Chip (aktiv /
/// abgelaufen / widerrufen), Downloads, Password-Icon, Copy + Teilen.
struct LinksView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var links: [ShareLinkDto] = []
    @State private var loading = true
    @State private var error: String?
    // v1.10.113: Löschbestätigung für einen Share-Link.
    @State private var pendingDelete: ShareLinkDto?

    var body: some View {
        Group {
            if loading && links.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, links.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                    Button("Erneut versuchen") { Task { await load() } }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if links.isEmpty {
                ContentUnavailableView("Noch keine Freigabelinks",
                                       systemImage: "link",
                                       description: Text("Erstelle Freigabelinks aus dem Dateien-Browser (rechtsklick / Kontext-Menü)."))
            } else {
                List {
                    let publicLinks = links.filter { $0.isPublic == true }
                    let mine = links.filter { $0.isPublic != true }
                    if !mine.isEmpty {
                        Section("👤 Privat") {
                            ForEach(mine) { linkRow($0) }
                        }
                    }
                    if !publicLinks.isEmpty {
                        Section("🌍 Öffentlich") {
                            ForEach(publicLinks) { linkRow($0) }
                        }
                    }
                }
            }
        }
        .navigationTitle("Meine Links")
        .task { await load() }
        .refreshable { await load() }
        // v1.10.113: Löschbestätigung.
        .alert("Link löschen?", isPresented: Binding(
            get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }
        )) {
            Button("Abbrechen", role: .cancel) { pendingDelete = nil }
            Button("Löschen", role: .destructive) {
                if let l = pendingDelete { Task { await delete(l) } }
                pendingDelete = nil
            }
        } message: {
            Text("Der Freigabelink wird dauerhaft entfernt. Die Datei selbst bleibt erhalten.")
        }
    }

    // v1.10.113: Row + Wisch/Kontext-Aktionen (Löschen, Kopieren, Teilen).
    // v1.10.158: Row ist jetzt NavigationLink zum LinkReportView. Bericht +
    // Löschen zusätzlich im ContextMenu / SwipeActions damit der einzelne Tap
    // nicht durch eine Button-Kaskade blockiert wird.
    @ViewBuilder
    private func linkRow(_ link: ShareLinkDto) -> some View {
        NavigationLink { LinkReportView(linkId: link.id, slug: link.slug) } label: { row(link) }
            .contextMenu {
                Button { UIPasteboard.general.string = link.url } label: {
                    Label("Link kopieren", systemImage: "doc.on.doc")
                }
                if let u = URL(string: link.url) {
                    ShareLink(item: u) { Label("Teilen", systemImage: "square.and.arrow.up") }
                }
                Button(role: .destructive) { pendingDelete = link } label: {
                    Label("Löschen", systemImage: "trash")
                }
            }
            .swipeActions(edge: .trailing) {
                Button(role: .destructive) { pendingDelete = link } label: {
                    Label("Löschen", systemImage: "trash")
                }
            }
    }

    private func delete(_ link: ShareLinkDto) async {
        guard let api = auth.api else { return }
        do { try await api.deleteShareLink(id: link.id); await load() }
        catch let ex { error = ex.localizedDescription }
    }

    private func row(_ link: ShareLinkDto) -> some View {
        let full = URL(string: link.url)
        // v1.10.71: target-Info als HEADLINE. Slug + URL zusätzlich klein
        // darunter (wie Web). Chip zeigt State.
        let targetLine: (icon: String, prefix: String, name: String)? = {
            if link.targetKind == "file" { return ("doc.text.fill", "Datei", link.targetName ?? "?") }
            if link.targetKind == "folder" { return ("folder.fill", "Ordner", link.targetName ?? "?") }
            return nil
        }()
        return VStack(alignment: .leading, spacing: 6) {
            if let t = targetLine {
                HStack(spacing: 6) {
                    Image(systemName: t.icon).foregroundStyle(Theme.tungstenBlue)
                    Text("\(t.prefix): ").foregroundStyle(.secondary)
                    Text(t.name).font(.body.weight(.semibold)).lineLimit(1)
                    Spacer()
                    statusChip(link)
                }
            } else {
                HStack {
                    Image(systemName: "link").foregroundStyle(Theme.tungstenBlue)
                    Text(link.slug).font(.body.weight(.medium)).lineLimit(1)
                    Spacer()
                    statusChip(link)
                }
            }
            HStack(spacing: 6) {
                Text(link.slug).font(.caption.monospaced()).foregroundStyle(Theme.tungstenBlue)
                if link.hasPassword { Image(systemName: "lock.fill").font(.caption).foregroundStyle(.secondary) }
                if link.isRevoked { Image(systemName: "xmark.circle.fill").font(.caption).foregroundStyle(Theme.warnRed) }
            }
            Text(link.url).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
            HStack(spacing: 12) {
                Text("\(link.downloadCount) Downloads").font(.caption).foregroundStyle(.secondary)
                if let limit = link.maxDownloads { Text("Limit: \(limit)").font(.caption).foregroundStyle(.secondary) }
                if let exp = link.expiresAt {
                    Text("Läuft ab: \(exp.formatted(date: .abbreviated, time: .omitted))")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            HStack(spacing: 8) {
                Button {
                    UIPasteboard.general.string = link.url
                } label: {
                    Label("Kopieren", systemImage: "doc.on.doc")
                }.buttonStyle(.bordered).controlSize(.small)
                if let full {
                    ShareLink(item: full) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }.buttonStyle(.bordered).controlSize(.small)
                }
            }
            .padding(.top, 2)
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private func statusChip(_ link: ShareLinkDto) -> some View {
        let now = Date()
        if link.isRevoked {
            chip("Widerrufen", color: Theme.warnRed)
        } else if let exp = link.expiresAt, exp <= now {
            chip("Abgelaufen", color: .orange)
        } else {
            chip("Aktiv", color: .green)
        }
    }
    private func chip(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2.weight(.medium))
            .padding(.horizontal, 6).padding(.vertical, 2)
            .background(color.opacity(0.15))
            .foregroundStyle(color)
            .clipShape(RoundedRectangle(cornerRadius: 3))
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { links = try await api.listMyLinks() }
        catch let e as ApiError {
            error = e.localizedDescription
            if case .notAuthorized = e { auth.signOut() }
        } catch let ex { error = ex.localizedDescription }
    }
}
