import SwiftUI
import CoreImage.CIFilterBuiltins

/// v1.10.71 / v1.10.148 / v1.11.18: 1:1-Parity mit Web-`/links`
/// (HomeController.Links). Drei Sektionen — 👤 Privat / 👥 Gruppen /
/// 🌍 Öffentlich — nach `ShareLinkDto.scope` (Server liefert seit v1.11.18
/// dieselbe Gesamtmenge wie Web: eigene Links + Public-Scope-Links anderer
/// Owner + eigene Group-Scope-Links, statt nur OwnerId==me). Fällt auf die
/// alte isPublic-basierte 2er-Aufteilung zurück, falls ein älterer Server
/// noch kein `scope`-Feld liefert. Jede Row mit „📄 Datei: X" oder
/// „📁 Ordner: Y" statt bloß Slug, plus Status-Chip (aktiv / abgelaufen /
/// widerrufen), Downloads, Password-/Seriennummer-Icon, Owner-Name bei
/// fremden Links, Copy + Teilen.
struct LinksView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var links: [ShareLinkDto] = []
    // v1.11.63: Web hat eine vierte Sektion "🌐 Subdomains" (Links.cshtml),
    // die Subdomain-ShareLinks UND Subdomain-Upload-Request-Links mischt —
    // iOS zeigte bislang keine von beiden. ShareLinks brauchten keine
    // Backend-Änderung (subdomainUrl kam schon mit); Upload-Requests
    // brauchten die Ergänzung in UploadRequestsController.List().
    @State private var uploadRequests: [NimShareAPI.UploadRequestListItemDto] = []
    @State private var loading = true
    @State private var error: String?
    // v1.10.113: Löschbestätigung für einen Share-Link.
    @State private var pendingDelete: ShareLinkDto?
    // v1.11.19: separat von `error` (das nur beim initialen Laden gerendert
    // wird, `.isEmpty`-Guard in body). Ein fehlgeschlagenes Widerrufen/
    // Löschen setzte vorher `error`, das aber bei nicht-leerer Liste NIE
    // angezeigt wurde — der Tap wirkte wie ein stiller No-Op. Review-Fund
    // dieser Session, behoben mit eigenem Alert.
    @State private var actionError: String?
    // v1.11.19: QR-Anzeige (nativ via CoreImage, kein SVG-Fetch nötig) +
    // "Per E-Mail senden" — beide existierten auf Web (Upload.cshtml-QR
    // bzw. Links.cshtml data-email), iOS hatte keine UI dafür.
    @State private var qrLink: ShareLinkDto?
    @State private var emailSheetLink: ShareLinkDto?
    // v1.11.50: Marcus's Wunsch — Suche über alle Sektionen (Web hat pro
    // Sektion ein eigenes Feld; .searchable ist der native iOS-Weg und
    // filtert alle drei Sektionen gleichzeitig, leere Sektionen blenden
    // sich dann von selbst aus).
    @State private var searchQuery = ""

    var body: some View {
        Group {
            if loading && links.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, links.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.danger2)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                    Button("Erneut versuchen") { Task { await load() } }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if links.isEmpty {
                RSEmptyState(systemImage: "link", title: "Noch keine Freigabelinks",
                             desc: "Erstelle Freigabelinks aus dem Dateien-Browser (rechtsklick / Kontext-Menü).")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    // v1.11.18: bevorzugt scope-basiert (private/group/public);
                    // Fallback auf isPublic für ältere Server-Antworten ohne
                    // das Feld.
                    let q = searchQuery.trimmingCharacters(in: .whitespaces).lowercased()
                    let visible = q.isEmpty ? links : links.filter { l in
                        l.slug.lowercased().contains(q)
                            || (l.targetName?.lowercased().contains(q) ?? false)
                            || (l.ownerName?.lowercased().contains(q) ?? false)
                    }
                    let hasScope = visible.contains { $0.scope != nil }
                    let privateLinks = hasScope
                        ? visible.filter { $0.scope == "private" || $0.scope == nil }
                        : visible.filter { $0.isPublic != true }
                    let groupLinks = hasScope ? visible.filter { $0.scope == "group" } : []
                    let publicLinks = hasScope
                        ? visible.filter { $0.scope == "public" }
                        : visible.filter { $0.isPublic == true }
                    if !privateLinks.isEmpty {
                        Section("👤 Privat") {
                            ForEach(privateLinks) { linkRow($0) }
                        }
                    }
                    if !groupLinks.isEmpty {
                        Section("👥 Gruppen") {
                            ForEach(groupLinks) { linkRow($0) }
                        }
                    }
                    if !publicLinks.isEmpty {
                        Section("🌍 Öffentlich") {
                            ForEach(publicLinks) { linkRow($0) }
                        }
                    }
                    // v1.11.63: Subdomain-Sektion, analog Web — cross-owner
                    // sichtbar (der Server liefert sie schon gemischt), mischt
                    // Share- und Upload-Request-Links wie Web es auch tut.
                    let subdomainShareLinks = visible.filter { ($0.subdomainUrl?.isEmpty == false) }
                    let visibleUploads = q.isEmpty ? uploadRequests : uploadRequests.filter { u in
                        u.slug.lowercased().contains(q) || (u.targetFolder?.lowercased().contains(q) ?? false)
                    }
                    let subdomainUploadLinks = visibleUploads.filter { ($0.subdomainUrl?.isEmpty == false) }
                    if !subdomainShareLinks.isEmpty || !subdomainUploadLinks.isEmpty {
                        Section("🌐 Subdomains") {
                            ForEach(subdomainShareLinks) { linkRow($0) }
                            ForEach(subdomainUploadLinks) { uploadRequestRow($0) }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle("Meine Links")
        .searchable(text: $searchQuery, prompt: "Slug, Datei/Ordner, Owner")
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
        // v1.11.19: Review-Fund — fehlgeschlagenes Widerrufen/Löschen/
        // Public-Toggle war vorher ein stiller No-Op (siehe `actionError`-
        // Deklaration oben). Jetzt sichtbarer Alert.
        .alert("Fehler", isPresented: Binding(
            get: { actionError != nil }, set: { if !$0 { actionError = nil } }
        )) {
            Button("OK", role: .cancel) { actionError = nil }
        } message: {
            Text(actionError ?? "")
        }
        .sheet(item: $qrLink) { link in
            QrCodeSheet(link: link)
        }
        .sheet(item: $emailSheetLink) { link in
            SendLinkByEmailSheet(link: link)
        }
    }

    // v1.10.113: Row + Wisch/Kontext-Aktionen (Löschen, Kopieren, Teilen).
    // v1.10.158: Row ist jetzt NavigationLink zum LinkReportView. Bericht +
    // Löschen zusätzlich im ContextMenu / SwipeActions damit der einzelne Tap
    // nicht durch eine Button-Kaskade blockiert wird.
    @ViewBuilder
    private func linkRow(_ link: ShareLinkDto) -> some View {
        // v1.11.0-UX-Fix: Kontext-Menü kopiert/teilt dieselbe primäre URL wie
        // die Row selbst (Subdomain wenn vorhanden, sonst die klassische).
        let primaryUrl = (link.subdomainUrl?.isEmpty == false) ? link.subdomainUrl! : link.url
        let canModerate = link.isOwnedByMe != false   // nil (alter Server) = eigener Link
        NavigationLink { LinkReportView(linkId: link.id, slug: link.slug) } label: { row(link) }
            .contextMenu {
                Button { UIPasteboard.general.string = primaryUrl } label: {
                    Label("Link kopieren", systemImage: "doc.on.doc")
                }
                if let u = URL(string: primaryUrl) {
                    ShareLink(item: u) { Label("Teilen", systemImage: "square.and.arrow.up") }
                }
                // v1.11.19: QR-Anzeige (nativ generiert aus primaryUrl) +
                // "Per E-Mail senden" — beides Web-Paritäts-Lücken, für
                // ALLE sichtbaren Links (auch read-only fremde Public-Links,
                // analog Web wo Copy/Teilen ebenfalls für jeden gehen).
                Button { qrLink = link } label: {
                    Label("QR-Code", systemImage: "qrcode")
                }
                if canModerate {
                    Button { emailSheetLink = link } label: {
                        Label("Per E-Mail senden", systemImage: "envelope")
                    }
                    // v1.11.18: Widerrufen als eigene Aktion getrennt von
                    // Löschen — deaktiviert den Link, ohne ihn samt
                    // Report-Historie zu entfernen (analog Web).
                    Button { Task { await toggleRevoke(link) } } label: {
                        Label(link.isRevoked ? "Wieder aktivieren" : "Widerrufen",
                              systemImage: link.isRevoked ? "arrow.uturn.backward.circle" : "xmark.circle")
                    }
                    Button(role: .destructive) { pendingDelete = link } label: {
                        Label("Löschen", systemImage: "trash")
                    }
                }
                // v1.11.19: Admin-only "öffentlich kuratiert machen"-Toggle,
                // analog Web (Links.cshtml isAdmin-Check). Unabhängig von
                // canModerate — ein Admin darf das auch auf fremden Links.
                if auth.user?.role == "Admin" {
                    Button { Task { await togglePublic(link) } } label: {
                        Label((link.isPublic ?? false) ? "Nicht mehr öffentlich" : "Öffentlich kuratieren",
                              systemImage: (link.isPublic ?? false) ? "lock" : "globe")
                    }
                }
            }
            .swipeActions(edge: .trailing) {
                if canModerate {
                    Button(role: .destructive) { pendingDelete = link } label: {
                        Label("Löschen", systemImage: "trash")
                    }
                    Button { Task { await toggleRevoke(link) } } label: {
                        Label(link.isRevoked ? "Aktivieren" : "Widerrufen",
                              systemImage: link.isRevoked ? "arrow.uturn.backward.circle" : "xmark.circle")
                    }.tint(Theme.yellow)
                }
            }
            .listRowBackground(Theme.surface2)
            .listRowSeparator(.hidden)
    }

    /// v1.11.63: leichtgewichtige Row für Upload-Request-Links in der
    /// Subdomains-Sektion — kein volles Kontext-Menü wie linkRow(), nur
    /// Kopieren/Teilen/Löschen (die Vollansicht bleibt UploadRequestsView).
    private func uploadRequestRow(_ u: NimShareAPI.UploadRequestListItemDto) -> some View {
        let primaryUrl = u.subdomainUrl ?? ""
        return VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Text("📥 \(u.targetFolder ?? u.slug)").font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                if u.isRevoked { miniChip("widerrufen", Theme.danger2) }
            }
            Text(primaryUrl).font(TFont.caption).foregroundStyle(Theme.textSecondary).lineLimit(1)
            Text("\(u.uploadCount) Uploads" + (u.isPermanent ? " · dauerhaft" : ""))
                .font(TFont.caption).foregroundStyle(Theme.textTertiary)
        }
        .padding(.vertical, 4)
        .listRowBackground(Theme.surface2)
        .listRowSeparator(.hidden)
        .contextMenu {
            Button { UIPasteboard.general.string = primaryUrl } label: {
                Label("Link kopieren", systemImage: "doc.on.doc")
            }
            if let url = URL(string: primaryUrl) {
                ShareLink(item: url) { Label("Teilen", systemImage: "square.and.arrow.up") }
            }
            Button(role: .destructive) { Task { await deleteUploadRequest(u) } } label: {
                Label("Löschen", systemImage: "trash")
            }
        }
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) { Task { await deleteUploadRequest(u) } } label: {
                Label("Löschen", systemImage: "trash")
            }
        }
    }

    private func miniChip(_ text: String, _ color: Color) -> some View {
        Text(text).font(TFont.caption).padding(.horizontal, 6).padding(.vertical, 2)
            .background(color.opacity(0.12)).foregroundStyle(color).clipShape(Capsule())
    }

    private func deleteUploadRequest(_ u: NimShareAPI.UploadRequestListItemDto) async {
        guard let api = auth.api else { return }
        do { try await api.deleteUploadRequest(u.id); await load() }
        catch is CancellationError {}
        catch let ex { actionError = ex.localizedDescription }
    }

    private func toggleRevoke(_ link: ShareLinkDto) async {
        guard let api = auth.api else { return }
        do { _ = try await api.updateShareLink(id: link.id, isRevoked: !link.isRevoked); await load() }
        catch is CancellationError {}
        catch let ex { actionError = ex.localizedDescription }
    }

    private func delete(_ link: ShareLinkDto) async {
        guard let api = auth.api else { return }
        do { try await api.deleteShareLink(id: link.id); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { actionError = ex.localizedDescription }
    }

    // v1.11.19: Admin-only "öffentlich kuratiert machen"-Toggle, analog
    // Web (Links.cshtml data-toggle-public, hinter isAdmin-Check).
    private func togglePublic(_ link: ShareLinkDto) async {
        guard let api = auth.api else { return }
        do { _ = try await api.updateShareLink(id: link.id, isPublic: !(link.isPublic ?? false)); await load() }
        catch is CancellationError {}
        catch let ex { actionError = ex.localizedDescription }
    }

    private func row(_ link: ShareLinkDto) -> some View {
        // v1.11.0-UX-Fix: wenn der Link eine Subdomain hat, ist SIE die
        // primäre/prominente URL (Copy/Teilen wirken darauf) — die klassische
        // /s/slug-URL wird nur noch als kleiner, dezenter Hinweis gezeigt statt
        // gleichwertig danebenzustehen ("das wär doppelt" — Marcus).
        let hasSubdomain = link.subdomainUrl?.isEmpty == false
        let primaryUrl = hasSubdomain ? link.subdomainUrl! : link.url
        let full = URL(string: primaryUrl)
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
                    Image(systemName: t.icon).foregroundStyle(Theme.navy)
                    Text("\(t.prefix): ").foregroundStyle(Theme.textSecondary)
                    Text(t.name).font(TFont.titleS).foregroundStyle(Theme.textPrimary).lineLimit(1)
                    Spacer()
                    statusChip(link)
                }
            } else {
                HStack {
                    Image(systemName: "link").foregroundStyle(Theme.navy)
                    Text(link.slug).font(TFont.titleS).foregroundStyle(Theme.textPrimary).lineLimit(1)
                    Spacer()
                    statusChip(link)
                }
            }
            HStack(spacing: 6) {
                Text(link.slug).font(.caption.monospaced()).foregroundStyle(Theme.cyan)
                if link.hasPassword { Image(systemName: "lock.fill").font(.caption).foregroundStyle(Theme.textTertiary) }
                // v1.11.18: Seriennummer-Indikator, analog Web (🔑-Zeile auf
                // der Landing) — zeigt dem Owner auf einen Blick, welche
                // Links eine hinterlegt haben.
                if link.hasSerialNumber == true { Image(systemName: "key.fill").font(.caption).foregroundStyle(Theme.textTertiary) }
                if link.isRevoked { Image(systemName: "xmark.circle.fill").font(.caption).foregroundStyle(Theme.danger2) }
            }
            // v1.11.18: Owner-Name bei fremden Links (Public/Group-Scope von
            // anderen Usern) — analog Web-Spalte "Owner.DisplayName" in
            // Links.cshtml. Bei eigenen Links (isOwnedByMe != false) leer.
            if link.isOwnedByMe == false, let owner = link.ownerName {
                Text("von \(owner)").font(TFont.caption).foregroundStyle(Theme.textSecondary)
            }
            HStack(spacing: 6) {
                if hasSubdomain { Image(systemName: "globe").font(.caption2).foregroundStyle(Theme.cyan) }
                Text(primaryUrl).font(.caption.monospaced())
                    .foregroundStyle(hasSubdomain ? Theme.cyan : Theme.textSecondary).lineLimit(1)
            }
            // v1.11.0-UX-Fix: klassische URL nur noch als kleiner, dezenter
            // Hinweis wenn die Subdomain bereits oben prominent gezeigt wird
            // (statt beide gleichwertig untereinander — "das wär doppelt").
            if hasSubdomain {
                Text("Auch erreichbar über \(link.url)")
                    .font(TFont.caption).foregroundStyle(Theme.textTertiary).lineLimit(1)
            }
            HStack(spacing: 12) {
                Text("\(link.downloadCount) Downloads").font(TFont.caption).foregroundStyle(Theme.textSecondary)
                if let limit = link.maxDownloads { Text("Limit: \(limit)").font(TFont.caption).foregroundStyle(Theme.textSecondary) }
                // v1.11.50: ♾️ statt Datum, wenn der Link explizit dauerhaft ist.
                if link.isPermanent == true {
                    Label("Dauerhaft", systemImage: "infinity")
                        .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                } else if let exp = link.expiresAt {
                    Text("Läuft ab: \(exp.formatted(date: .abbreviated, time: .omitted))")
                        .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                }
            }
            HStack(spacing: 8) {
                Button {
                    UIPasteboard.general.string = primaryUrl
                } label: {
                    Label("Kopieren", systemImage: "doc.on.doc")
                }.buttonStyle(.bordered).controlSize(.small).tint(Theme.navy)
                if let full {
                    ShareLink(item: full) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }.buttonStyle(.bordered).controlSize(.small).tint(Theme.navy)
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
            chip("Widerrufen", color: Theme.danger2)
        } else if let exp = link.expiresAt, exp <= now {
            chip("Abgelaufen", color: Theme.yellow)
        } else {
            chip("Aktiv", color: Theme.success2)
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
        do {
            async let l = api.listMyLinks()
            async let u = api.listUploadRequests()
            links = try await l
            uploadRequests = try await u
        }
        catch let e as ApiError {
            error = e.localizedDescription
            if case .notAuthorized = e { auth.signOut() }
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}

// v1.11.19: QR-Anzeige — nativ via CoreImage statt Fetch des Server-SVGs
// (SwiftUI kann SVG nicht direkt rendern; ein WKWebView-Umweg wäre für ein
// simples QR unnötig schwer). Kodiert dieselbe URL, die Web im SVG-Endpoint
// erzeugt (BuildPublicUrl(l.Slug)) — Ergebnis ist pixelidentisch nutzbar.
private struct QrCodeSheet: View {
    @Environment(\.dismiss) private var dismiss
    let link: ShareLinkDto

    private var primaryUrl: String {
        (link.subdomainUrl?.isEmpty == false) ? link.subdomainUrl! : link.url
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 16) {
                if let img = Self.generate(from: primaryUrl) {
                    Image(uiImage: img)
                        .interpolation(.none)
                        .resizable()
                        .frame(width: 240, height: 240)
                        .padding()
                        .background(Color.white)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                        .shadow(radius: 2)
                } else {
                    ContentUnavailableView("QR-Code konnte nicht erzeugt werden", systemImage: "qrcode")
                }
                Text(primaryUrl).font(.caption.monospaced()).foregroundStyle(.secondary)
                    .multilineTextAlignment(.center).padding(.horizontal)
                if let img = Self.generate(from: primaryUrl) {
                    ShareLink(item: Image(uiImage: img), preview: SharePreview("QR-Code", image: Image(uiImage: img))) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }.buttonStyle(.bordered)
                }
            }
            .padding()
            .navigationTitle("QR-Code")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Schließen") { dismiss() } }
            }
        }
    }

    private static func generate(from string: String) -> UIImage? {
        guard let data = string.data(using: .utf8) else { return nil }
        let filter = CIFilter.qrCodeGenerator()
        filter.message = data
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        // Kleines CIImage hochskalieren, sonst wirkt der Code beim Rendern
        // verwaschen (Standard-Output ist nur ~wenige Dutzend Pixel groß).
        let scaled = output.transformed(by: CGAffineTransform(scaleX: 10, y: 10))
        let context = CIContext()
        guard let cgImage = context.createCGImage(scaled, from: scaled.extent) else { return nil }
        return UIImage(cgImage: cgImage)
    }
}

// v1.11.19: "Per E-Mail senden" — Server-Endpoint existiert seit v1.10.x
// (LinksController.SendByEmail), iOS rief ihn nie auf (Web: Links.cshtml
// data-email-Button + Modal).
private struct SendLinkByEmailSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let link: ShareLinkDto

    @State private var toEmail = ""
    @State private var message = ""
    @State private var busy = false
    @State private var error: String?
    @State private var sent = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Empfänger") {
                    TextField("E-Mail", text: $toEmail)
                        .keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                Section("Nachricht (optional)") {
                    TextField("Kurze Nachricht", text: $message, axis: .vertical).lineLimit(2...5)
                }
                if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
                if sent { Section { Label("Gesendet!", systemImage: "checkmark.circle.fill").foregroundStyle(.green) } }
                Section {
                    Button("Senden") { Task { await send() } }
                        .disabled(toEmail.isEmpty || !toEmail.contains("@") || busy)
                }
            }
            .navigationTitle("Per E-Mail senden")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Schließen") { dismiss() } }
            }
            .overlay { if busy { ProgressView() } }
        }
    }

    private func send() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            try await api.sendShareLinkByEmail(id: link.id, toEmail: toEmail.trimmingCharacters(in: .whitespacesAndNewlines), message: message.isEmpty ? nil : message)
            sent = true
            toEmail = ""; message = ""
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}
