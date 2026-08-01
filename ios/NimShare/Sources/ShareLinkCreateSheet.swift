import SwiftUI

/// v1.10.71: Voll-Feature-Sheet für Freigabelink erstellen.
/// Web-Parity: Slug (optional), Password (optional), Max-Downloads,
/// Ablaufdatum, Nachricht, Notify-on-Access. Nach Create wird die URL
/// im gleichen Sheet gezeigt mit Copy/Teilen — kein Modal-Springen mehr.
struct ShareLinkCreateSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    enum Target: Identifiable {
        case file(UUID), folder(UUID)
        var id: String {
            switch self { case .file(let id): return "f-\(id)"; case .folder(let id): return "d-\(id)" }
        }
    }
    let target: Target
    let itemName: String

    @State private var slug = ""
    @State private var password = ""
    @State private var maxDownloadsText = ""
    // v1.11.50: Marcus's Wunsch — Default ist jetzt "läuft in 8 Wochen ab",
    // "Dauerhaft" muss aktiv angeklickt werden (Web hat dieselbe Umkehr).
    @State private var isPermanent = false
    @State private var expiryDate = Date().addingTimeInterval(60*60*24*56)
    @State private var message = ""
    @State private var notifyOnAccess = false
    // v1.11.18: optionale Seriennummer/Lizenzcode — analog Web-Modal.
    @State private var serialNumber = ""

    @State private var busy = false
    @State private var error: String?
    @State private var result: ShareLinkDto?
    // v1.10.146: Zertifikat-Picker.
    @State private var certs: [CertDto] = []
    @State private var selectedCertId: UUID? = nil
    // v1.10.172: Foto/Video-Album-Anzeige + „Upload erlauben" — 1:1-Parität
    // zum Web-Share-Modal. Nur bei Folder-Targets sichtbar; auf File-Links
    // würde der Server das Flag ohnehin ignorieren.
    @State private var displayAsGallery: Bool = false
    @State private var allowUploads: Bool = false
    // v1.11.0: Subdomain-Sharing — Info einmal laden, Sektion nur zeigen
    // wenn Feature an UND dieser User es nutzen darf.
    @State private var subInfo: NimShareAPI.SubdomainInfo?
    @State private var useSubdomain = false
    @State private var subSlug = ""
    @State private var subCheck: NimShareAPI.SubdomainCheck?

    private var isFolderTarget: Bool {
        if case .folder = target { return true } else { return false }
    }

    var body: some View {
        NavigationStack {
            if let r = result {
                resultView(r)
            } else {
                formView
                    .task {
                        guard let api = auth.api else { return }
                        // v1.11.0: Subdomain-Feature-Info (einmalig).
                        if subInfo == nil {
                            subInfo = try? await api.subdomainInfo()
                        }
                        // Zertifikate nachladen; Default vorwählen falls vorhanden.
                        guard certs.isEmpty else { return }
                        if let list = try? await api.listCertificates() {
                            certs = list
                            selectedCertId = list.first(where: { $0.isDefault })?.id
                        }
                    }
            }
        }
    }

    private var formView: some View {
        Form {
            Section {
                HStack {
                    Image(systemName: {
                        switch target { case .file: return "doc.text.fill"; case .folder: return "folder.fill" }
                    }())
                    .foregroundStyle(Theme.tungstenBlue)
                    Text(itemName).font(.body.weight(.semibold)).lineLimit(1)
                }
            }
            Section("Slug (optional)") {
                TextField("z.B. quartalsreport", text: $slug)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                Text("Freilassen für automatisch generierten Slug.").font(.caption).foregroundStyle(.secondary)
            }
            // v1.11.0: Subdomain-Sharing — nur wenn Server-Feature an
            // UND dieser User es nutzen darf.
            if let si = subInfo, si.enabled, si.canUse {
                SubdomainSection(info: si,
                                 enabled: $useSubdomain,
                                 slug: $subSlug,
                                 check: $subCheck)
            }
            Section("Passwortschutz (optional)") {
                SecureField("Passwort", text: $password)
                    .textContentType(.newPassword)
            }
            Section("Limits") {
                TextField("Max. Downloads (leer = unbegrenzt)", text: $maxDownloadsText)
                    .keyboardType(.numberPad)
                if !isPermanent {
                    DatePicker("Läuft ab am", selection: $expiryDate, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                }
                Toggle("♾️ Dauerhaft (nie ablaufen)", isOn: $isPermanent)
            }
            Section("Nachricht (optional)") {
                TextField("Kurze Nachricht für den Empfänger", text: $message, axis: .vertical)
                    .lineLimit(2...5)
            }
            Section {
                Toggle("Benachrichtigen wenn Link geöffnet wird", isOn: $notifyOnAccess)
            }
            // v1.10.172: Foto/Video-Album-Sektion — nur für Folder-Targets,
            // Sub-Toggle „Upload erlauben" erst wenn Album-Modus an ist.
            if isFolderTarget {
                Section("Foto/Video-Album") {
                    Toggle(isOn: $displayAsGallery.animation()) {
                        Label("Als Foto/Video-Album anzeigen", systemImage: "photo.on.rectangle.angled")
                    }
                    .onChange(of: displayAsGallery) { _, on in
                        if !on { allowUploads = false }
                    }
                    if displayAsGallery {
                        Toggle(isOn: $allowUploads) {
                            Label("Empfänger dürfen hochladen", systemImage: "square.and.arrow.up")
                        }
                        Text("Ideal für Hochzeits- oder Event-Alben — jeder mit dem Link kann Fotos/Videos direkt hinzufügen.")
                            .font(.caption).foregroundStyle(.secondary)
                    } else {
                        Text("Empfänger sehen ein Grid mit Vorschauen und Lightbox statt der klassischen Datei-Liste.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            // v1.10.146: Absender-Zertifikat (nur wenn welche vorhanden).
            if !certs.isEmpty {
                Section("Absender-Zertifikat (optional)") {
                    Picker("Zertifikat", selection: $selectedCertId) {
                        Text("Kein Zertifikat").tag(UUID?.none)
                        ForEach(certs, id: \.id) { c in
                            Text(c.name + (c.isDefault ? " ★" : "")).tag(UUID?.some(c.id))
                        }
                    }
                    Text("Zertifikat anhängen, damit der Empfänger sieht, von wem der Link ist.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            // v1.11.18: optionale Seriennummer — z.B. bei Software-Downloads.
            // Wird serverseitig verschlüsselt, auf der Landing erst nach
            // Klick angezeigt + optional per E-Mail zugesendet.
            Section("Seriennummer (optional)") {
                TextField("z.B. XXXX-XXXX-XXXX-XXXX", text: $serialNumber)
                    .textInputAutocapitalization(.characters)
                    .autocorrectionDisabled()
                Text("Wird auf der Landing-Seite erst nach Klick des Empfängers angezeigt.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
            Section {
                Button("Freigabelink erstellen") { Task { await create() } }
                    .frame(maxWidth: .infinity)
                    .disabled(busy)
            }
        }
        .navigationTitle("Freigabelink")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
        }
        .overlay { if busy { ProgressView() } }
    }

    private func resultView(_ r: ShareLinkDto) -> some View {
        VStack(spacing: 16) {
            Image(systemName: "checkmark.seal.fill")
                .font(.system(size: 48))
                .foregroundStyle(.green)
                .padding(.top, 24)
            Text("Link erstellt").font(.title2.weight(.bold))
            // v1.11.0-UX-Fix: vorher wurden klassische /s/slug-URL UND
            // Subdomain-URL gleichwertig nebeneinander gezeigt — Marcus
            // fand das "doppelt". Jetzt: wenn eine Subdomain existiert,
            // IST SIE die primäre/prominente URL; die klassische URL wird
            // nur noch als kleiner, dezenter Hinweis darunter erwähnt.
            ShareResultUrlBlock(classicUrl: r.url, subdomainUrl: r.subdomainUrl)
            Spacer()
        }
        .padding()
        .navigationTitle("Fertig")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) { Button("Schließen") { dismiss() } }
        }
    }

    private func create() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        let maxDl = Int(maxDownloadsText.trimmingCharacters(in: .whitespaces))
        do {
            let fileId: UUID?; let folderId: UUID?
            switch target {
            case .file(let id): fileId = id; folderId = nil
            case .folder(let id): fileId = nil; folderId = id
            }
            let link = try await api.createShareLinkFull(
                fileId: fileId, folderId: folderId,
                slug: slug.isEmpty ? nil : slug,
                password: password.isEmpty ? nil : password,
                maxDownloads: maxDl,
                expiresAt: isPermanent ? nil : expiryDate,
                message: message.isEmpty ? nil : message,
                notifyOnAccess: notifyOnAccess,
                signingCertificateId: selectedCertId,    // v1.10.146
                displayAsGallery: isFolderTarget && displayAsGallery ? true : nil,  // v1.10.172
                allowUploads: isFolderTarget && displayAsGallery && allowUploads ? true : nil,
                subdomainSlug: effectiveSubdomainSlug(useSubdomain: useSubdomain, slug: subSlug),  // v1.11.0
                serialNumber: serialNumber.trimmingCharacters(in: .whitespaces).isEmpty ? nil : serialNumber.trimmingCharacters(in: .whitespaces),  // v1.11.18
                isPermanent: isPermanent  // v1.11.50
            )
            result = link
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}

// MARK: - v1.11.0 Subdomain-Sharing (geteilt von beiden Sheets)

/// Nur senden wenn der Toggle an ist UND ein Slug eingetippt wurde — sonst
/// nil, damit ältere Server das Feld gar nicht erst sehen.
func effectiveSubdomainSlug(useSubdomain: Bool, slug: String) -> String? {
    guard useSubdomain else { return nil }
    let t = slug.trimmingCharacters(in: .whitespaces)
    return t.isEmpty ? nil : t
}

/// v1.11.0: Form-Sektion „Als Subdomain freigeben" mit Live-Verfügbarkeits-
/// Check (~400ms Debounce, Task-Cancel-Muster wie die User-Suche im
/// DirectShareSheet). Grüne Zeile mit voller Host-Vorschau bei verfügbar,
/// rote Zeile mit Grund (vergeben / reserviert / ungültig) sonst.
struct SubdomainSection: View {
    @EnvironmentObject var auth: AuthStore
    let info: NimShareAPI.SubdomainInfo
    @Binding var enabled: Bool
    @Binding var slug: String
    @Binding var check: NimShareAPI.SubdomainCheck?
    /// In-flight Check-Task, damit jeder Tastendruck den vorherigen abbricht.
    @State private var checkTask: Task<Void, Never>?

    var body: some View {
        Section {
            Toggle(isOn: $enabled.animation()) {
                Label("Als Subdomain freigeben", systemImage: "globe")
            }
            .onChange(of: enabled) { _, on in
                if !on { checkTask?.cancel(); check = nil }
            }
            if enabled {
                TextField("z.B. wichtig", text: $slug)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                    .onChange(of: slug) { _, new in
                        checkTask?.cancel()
                        check = nil
                        let t = new.trimmingCharacters(in: .whitespaces)
                        guard !t.isEmpty else { return }
                        checkTask = Task { await runCheck(t) }
                    }
                if let c = check {
                    if c.available {
                        Text("✓ \(host(c.normalised))")
                            .font(.caption).foregroundStyle(.green)
                    } else {
                        Text("✕ \(reasonText(c.reason))")
                            .font(.caption).foregroundStyle(Theme.danger2)
                    }
                }
            }
        }
    }

    /// „wichtig" + Basis-Domain → „wichtig.nimshare.com".
    private func host(_ normalised: String) -> String {
        guard let base = info.baseDomain, !base.isEmpty else { return normalised }
        return "\(normalised).\(base)"
    }

    private func reasonText(_ reason: String?) -> String {
        switch reason {
        case "taken": return String(localized: "vergeben")
        case "reserved": return String(localized: "reserviert")
        default: return String(localized: "ungültig")
        }
    }

    private func runCheck(_ slug: String) async {
        try? await Task.sleep(nanoseconds: 400_000_000)
        if Task.isCancelled { return }
        guard let api = auth.api else { return }
        do {
            let r = try await api.subdomainCheck(slug)
            if !Task.isCancelled { check = r }
        } catch {
            // Netzfehler/Cancel → keine Zeile anzeigen statt Fehl-Alarm.
            if !Task.isCancelled { check = nil }
        }
    }
}

/// v1.11.0-UX-Fix: gemeinsame Ergebnis-URL-Anzeige für Freigabelink UND
/// Upload-Anforderung. Ersetzt die vorherige Doppel-Anzeige (klassische URL
/// prominent + Subdomain-URL zusätzlich in einem eigenen Block darunter, was
/// wie zwei separate Links wirkte). Jetzt: genau EINE prominente URL zum
/// Kopieren/Teilen — Subdomain wenn vorhanden, sonst die klassische — plus
/// optional ein kleiner, dezenter Hinweis, dass die klassische URL daneben
/// ebenfalls weiter funktioniert.
struct ShareResultUrlBlock: View {
    let classicUrl: String
    let subdomainUrl: String?

    private var hasSubdomain: Bool { subdomainUrl?.isEmpty == false }
    private var primaryUrl: String { hasSubdomain ? subdomainUrl! : classicUrl }

    var body: some View {
        VStack(spacing: 8) {
            Text(primaryUrl).font(.footnote.monospaced()).foregroundStyle(.secondary)
                .padding(.horizontal, 20).multilineTextAlignment(.center)
            HStack(spacing: 12) {
                Button {
                    UIPasteboard.general.string = primaryUrl
                } label: {
                    Label("Kopieren", systemImage: "doc.on.doc")
                }.buttonStyle(.borderedProminent).tint(Theme.tungstenBlue)
                if let u = URL(string: primaryUrl) {
                    ShareLink(item: u) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }.buttonStyle(.bordered).tint(Theme.tungstenBlue)
                }
            }
            if hasSubdomain {
                Text("Auch erreichbar über \(classicUrl)")
                    .font(.caption2).foregroundStyle(.secondary)
                    .padding(.horizontal, 20).multilineTextAlignment(.center)
            }
        }
    }
}

/// v1.10.71: Voll-Feature-Sheet für Upload-Anforderung. Web-parity mit
/// allen Optionen (Slug, Passwort, Max-Uploads, Ablauf, Nachricht, Notify).
struct UploadRequestCreateSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    // v1.10.113: optionaler Ziel-Ordnername (Long-Press „Upload anfordern"
    // auf einem Ordner). nil → Server-Default „Received".
    var targetFolderName: String? = nil

    @State private var slug = ""
    @State private var password = ""
    @State private var maxUploadsText = ""
    // v1.11.50: analog ShareLinkCreateSheet — Default 8 Wochen, "Dauerhaft"
    // muss aktiv angeklickt werden.
    @State private var isPermanent = false
    @State private var expiryDate = Date().addingTimeInterval(60*60*24*56)
    @State private var message = ""
    @State private var notifyOnUpload = true

    @State private var busy = false
    @State private var error: String?
    @State private var result: NimShareAPI.UploadRequestResult?
    // v1.10.146: Zertifikat-Picker.
    @State private var certs: [CertDto] = []
    @State private var selectedCertId: UUID? = nil
    // v1.11.0: Subdomain-Sharing (identisches Muster wie ShareLinkCreateSheet).
    @State private var subInfo: NimShareAPI.SubdomainInfo?
    @State private var useSubdomain = false
    @State private var subSlug = ""
    @State private var subCheck: NimShareAPI.SubdomainCheck?

    var body: some View {
        NavigationStack {
            if let r = result {
                resultView(r)
            } else {
                formView
                    .task {
                        guard let api = auth.api else { return }
                        // v1.11.0: Subdomain-Feature-Info (einmalig).
                        if subInfo == nil {
                            subInfo = try? await api.subdomainInfo()
                        }
                        guard certs.isEmpty else { return }
                        if let list = try? await api.listCertificates() {
                            certs = list
                            selectedCertId = list.first(where: { $0.isDefault })?.id
                        }
                    }
            }
        }
    }

    private var formView: some View {
        Form {
            Section("Slug (optional)") {
                TextField("z.B. jan-invoices", text: $slug)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                Text("Freilassen für automatisch generierten Slug.").font(.caption).foregroundStyle(.secondary)
            }
            // v1.11.0: Subdomain-Sharing — nur wenn Server-Feature an
            // UND dieser User es nutzen darf.
            if let si = subInfo, si.enabled, si.canUse {
                SubdomainSection(info: si,
                                 enabled: $useSubdomain,
                                 slug: $subSlug,
                                 check: $subCheck)
            }
            Section("Passwortschutz (optional)") {
                SecureField("Passwort", text: $password).textContentType(.newPassword)
            }
            Section("Limits") {
                TextField("Max. Uploads (leer = unbegrenzt)", text: $maxUploadsText)
                    .keyboardType(.numberPad)
                if !isPermanent {
                    DatePicker("Läuft ab am", selection: $expiryDate, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                }
                Toggle("♾️ Dauerhaft (nie ablaufen)", isOn: $isPermanent)
            }
            Section("Nachricht an den Empfänger") {
                TextField("Was soll hochgeladen werden?", text: $message, axis: .vertical)
                    .lineLimit(2...5)
            }
            Section {
                Toggle("Bei Upload benachrichtigen", isOn: $notifyOnUpload)
            }
            // v1.10.146: Absender-Zertifikat (nur wenn welche vorhanden).
            if !certs.isEmpty {
                Section("Absender-Zertifikat (optional)") {
                    Picker("Zertifikat", selection: $selectedCertId) {
                        Text("Kein Zertifikat").tag(UUID?.none)
                        ForEach(certs, id: \.id) { c in
                            Text(c.name + (c.isDefault ? " ★" : "")).tag(UUID?.some(c.id))
                        }
                    }
                    Text("Zertifikat anhängen, damit der Empfänger sieht, von wem der Link ist.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            if let e = error { Section { Text(e).foregroundStyle(Theme.danger2) } }
            Section {
                Button("Upload-Anforderung erstellen") { Task { await create() } }
                    .frame(maxWidth: .infinity)
                    .disabled(busy)
            }
        }
        .navigationTitle("Upload anfordern")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
        }
        .overlay { if busy { ProgressView() } }
    }

    private func resultView(_ r: NimShareAPI.UploadRequestResult) -> some View {
        VStack(spacing: 16) {
            Image(systemName: "checkmark.seal.fill")
                .font(.system(size: 48)).foregroundStyle(.green).padding(.top, 24)
            Text("Anforderung erstellt").font(.title2.weight(.bold))
            // v1.11.0-UX-Fix: siehe ShareLinkCreateSheet.resultView — Subdomain
            // wird primär, klassische URL nur noch als dezenter Hinweis.
            ShareResultUrlBlock(classicUrl: r.url, subdomainUrl: r.subdomainUrl)
            Spacer()
        }
        .padding()
        .navigationTitle("Fertig")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { ToolbarItem(placement: .topBarTrailing) { Button("Schließen") { dismiss() } } }
    }

    private func create() async {
        guard let api = auth.api else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            result = try await api.createUploadRequestFull(
                slug: slug.isEmpty ? nil : slug,
                password: password.isEmpty ? nil : password,
                maxUploads: Int(maxUploadsText.trimmingCharacters(in: .whitespaces)),
                expiresAt: isPermanent ? nil : expiryDate,
                message: message.isEmpty ? nil : message,
                targetFolder: (targetFolderName?.isEmpty == false) ? targetFolderName! : "Received",
                notifyOnUpload: notifyOnUpload,
                signingCertificateId: selectedCertId,    // v1.10.146
                subdomainSlug: effectiveSubdomainSlug(useSubdomain: useSubdomain, slug: subSlug),  // v1.11.0
                isPermanent: isPermanent  // v1.11.50
            )
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
