import SwiftUI
import UniformTypeIdentifiers

/// Requester-side list of the signed-in user's signature requests. Tap
/// "Neue" to start a wizard; tap a row to open the on-server detail page.
struct SignaturesView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [SignatureRequestDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var showNew = false

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView("Keine Signatur-Anforderungen",
                    systemImage: "signature",
                    description: Text("Sende ein PDF an eine Person, damit sie unterschreibt."))
            } else {
                // v1.10.56: 3 Sektionen wie im Web — "In Bearbeitung"
                // (Draft+Sent), "Abgeschlossen" (Completed), "Abgelehnt/
                // Abgebrochen" (Declined+Cancelled). Tap → SignatureDetailView.
                let active = items.filter { $0.status == "Draft" || $0.status == "Sent" }
                let completed = items.filter { $0.status == "Completed" }
                let abandoned = items.filter { $0.status == "Declined" || $0.status == "Cancelled" }
                List {
                    if !active.isEmpty {
                        Section("🔄 In Bearbeitung") {
                            ForEach(active) { r in rowLink(r) }
                        }
                    }
                    if !completed.isEmpty {
                        Section("✅ Abgeschlossen") {
                            ForEach(completed) { r in rowLink(r) }
                        }
                    }
                    if !abandoned.isEmpty {
                        Section("✕ Abgelehnt / abgebrochen") {
                            ForEach(abandoned) { r in rowLink(r) }
                        }
                    }
                }
                .listStyle(.insetGrouped)
            }
            if let e = error {
                Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding()
            }
        }
        .navigationTitle("Signaturen")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showNew = true } label: {
                    Image(systemName: "plus")
                }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(isPresented: $showNew) {
            NewSignatureRequestSheet(onDone: {
                showNew = false
                Task { await load() }
            })
        }
    }

    @ViewBuilder
    private func rowLink(_ r: SignatureRequestDto) -> some View {
        NavigationLink(destination: SignatureDetailView(requestId: r.id, initialTitle: r.title)) {
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(r.title).font(.body.weight(.semibold)).lineLimit(2)
                    Spacer()
                    statusBadge(r.status)
                }
                Text("\(r.participants.filter { $0.status == "Signed" || ($0.role == "Viewer" && $0.status == "Viewed") }.count) / \(r.participants.count) fertig")
                    .font(.caption).foregroundStyle(.secondary)
                Text(r.createdAt.formatted(date: .abbreviated, time: .shortened))
                    .font(.caption2).foregroundStyle(.secondary)
            }
            .padding(.vertical, 2)
        }
    }

    private func statusBadge(_ status: String) -> some View {
        let (color, label): (Color, String)
        switch status {
        case "Completed": (color, label) = (.green, "Fertig")
        case "Sent":      (color, label) = (.orange, "Läuft")
        case "Declined":  (color, label) = (Theme.warnRed, "Abgelehnt")
        case "Cancelled": (color, label) = (.gray, "Zurückgezogen")
        default:          (color, label) = (.gray, "Entwurf")
        }
        return Text(label)
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
        do { items = try await api.listMySignatureRequests() }
        catch let ex { error = ex.localizedDescription }
    }
}

/// Two-step sheet: (1) pick PDF + metadata, (2) add participants. Field
/// placement is anchor-preset only — the visual pdf.js placement lives on
/// the web; iOS gets it in a future release.
struct NewSignatureRequestSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    var onDone: () -> Void

    @State private var step = 1
    @State private var scopes: [ScopeTile] = []
    @State private var files: [FileItem] = []
    @State private var pickedFileId: UUID?
    @State private var title = ""
    @State private var message = ""

    @State private var participants: [Participant] = []
    @State private var newEmail = ""
    @State private var newName = ""
    @State private var newRole = "Signer"
    // v1.10.71: Delivery-Order + Deadline (Web-Parity)
    @State private var deliveryOrder: String = "Parallel"
    @State private var useDeadline = false
    @State private var deadline = Date().addingTimeInterval(60*60*24*7)
    // v1.10.71: Kontakt-Vorschläge aus Adressbuch
    @State private var contactSuggestions: [ContactDto] = []
    // v1.10.88: Email-Template-Picker
    @State private var emailTemplates: [NimShareAPI.EmailTemplateDto] = []
    @State private var pickedTemplateId: UUID?

    @State private var requestId: UUID?
    @State private var busy = false
    @State private var error: String?

    struct Participant: Identifiable {
        let id = UUID(); let email: String; let name: String; let role: String; let serverId: UUID
    }

    var body: some View {
        NavigationStack {
            Group {
                switch step {
                case 1: stepOne
                case 2: stepTwo
                default: EmptyView()
                }
            }
            .navigationTitle(step == 1 ? "Dokument" : "Empfänger")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Abbrechen") { dismiss() }
                }
                if step == 2 {
                    ToolbarItem(placement: .topBarTrailing) {
                        Button("Absenden") { Task { await send() } }
                            .disabled(participants.filter { $0.role == "Signer" }.isEmpty || busy)
                    }
                }
            }
            .task {
                if scopes.isEmpty { await loadPersonalFiles() }
                // v1.10.88: Email-Templates für Signatur-Einladung nachladen
                if let api = auth.api, emailTemplates.isEmpty {
                    if let ts = try? await api.listEmailTemplates(kind: "SignatureInvite") {
                        emailTemplates = ts
                    }
                }
            }
            .overlay { if busy { ProgressView() } }
            .fileImporter(
                isPresented: $showPicker,
                allowedContentTypes: [.pdf],
                allowsMultipleSelection: false
            ) { result in
                Task { await handlePickedFile(result) }
            }
        }
    }

    /// v1.10.70: Importierte PDF hochladen und automatisch als
    /// pickedFileId setzen. Die Datei landet in Personal-Root (folderId=nil
    /// → Server nimmt den Personal-Root-Folder des Users).
    private func handlePickedFile(_ result: Result<[URL], Error>) async {
        guard let api = auth.api else { return }
        do {
            let urls = try result.get()
            guard let src = urls.first else { return }
            // Security-scoped-URL access: iCloud/Dateien-App-URLs brauchen
            // explizites startAccessingSecurityScopedResource() um sie zu lesen.
            let didStart = src.startAccessingSecurityScopedResource()
            defer { if didStart { src.stopAccessingSecurityScopedResource() } }
            let data = try Data(contentsOf: src)
            busy = true; error = nil
            let name = src.lastPathComponent
            let fid = try await api.uploadFile(name: name, contentType: "application/pdf",
                folderId: nil, data: data)
            // Neu geladene File in die lokale Liste einreihen + auswählen
            files.append(FileItem(id: fid, name: name, sizeBytes: Int64(data.count),
                contentType: "application/pdf", createdAt: Date(), ownerName: nil,
                aiTags: nil, aiRiskFlag: nil))
            pickedFileId = fid
        } catch let ex { error = ex.localizedDescription }
        busy = false
    }

    @State private var showPicker = false

    @ViewBuilder
    private var stepOne: some View {
        Form {
            Section("PDF wählen") {
                let pdfs = files.filter { $0.contentType.contains("pdf") }
                if pdfs.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Keine PDFs in deinen Bibliotheken gefunden.").foregroundStyle(.secondary)
                        Text("Wähle unten eine PDF aus der Dateien-App / iCloud Drive — sie wird direkt in deine Personal-Ablage geladen.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                } else {
                    Picker("Datei", selection: $pickedFileId) {
                        Text("—").tag(UUID?.none)
                        ForEach(pdfs) { f in
                            Text(f.name).tag(Optional(f.id))
                        }
                    }
                }
                // v1.10.70: PDF direkt aus Dateien-App wählen. Nach Upload
                // wird die neue File-ID sofort als pickedFileId gesetzt, User
                // kann direkt "Weiter" drücken.
                Button {
                    showPicker = true
                } label: {
                    Label("PDF aus Dateien wählen…", systemImage: "doc.badge.plus")
                }
                .disabled(busy)
            }
            Section("Titel & Nachricht") {
                TextField("Titel (Standard: Dateiname)", text: $title)
                TextField("Nachricht an die Empfänger", text: $message, axis: .vertical).lineLimit(2...5)
                // v1.10.88: Email-Template-Picker — Parität zum Web-Wizard.
                // Zeigt die persönlichen Templates mit Kind=SignatureInvite;
                // Auswahl übernimmt Subject → Titel, Body → Nachricht.
                if !emailTemplates.isEmpty {
                    Picker("Vorlage laden", selection: $pickedTemplateId) {
                        Text("— keine —").tag(UUID?.none)
                        ForEach(emailTemplates) { t in
                            Text(t.name).tag(Optional(t.id))
                        }
                    }
                    .onChange(of: pickedTemplateId) { _, new in
                        if let id = new, let t = emailTemplates.first(where: { $0.id == id }) {
                            if title.isEmpty { title = t.subject }
                            if message.isEmpty { message = t.bodyMarkdown }
                        }
                    }
                }
            }
            Section("Ablauf") {
                Picker("Reihenfolge", selection: $deliveryOrder) {
                    Text("Alle parallel").tag("Parallel")
                    Text("Nacheinander").tag("Sequential")
                }
                Toggle("Frist setzen", isOn: $useDeadline)
                if useDeadline {
                    DatePicker("Deadline", selection: $deadline, in: Date()..., displayedComponents: [.date])
                }
            }
            if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
            Section {
                Button("Weiter →") { Task { await createDraft() } }
                    .disabled(pickedFileId == nil || busy)
            }
        }
    }

    @ViewBuilder
    private var stepTwo: some View {
        Form {
            if !contactSuggestions.isEmpty {
                Section("Aus Adressbuch") {
                    ForEach(contactSuggestions.prefix(5)) { c in
                        Button {
                            newEmail = c.email
                            newName = c.name
                        } label: {
                            HStack {
                                Image(systemName: "person.crop.circle").foregroundStyle(Theme.tungstenBlue)
                                VStack(alignment: .leading) {
                                    Text(c.name).foregroundStyle(.primary)
                                    Text(c.email).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                Image(systemName: "arrow.up.left.circle").foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
            Section("Empfänger hinzufügen") {
                TextField("E-Mail", text: $newEmail).keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                TextField("Name", text: $newName)
                Picker("Rolle", selection: $newRole) {
                    Text("✍ Unterzeichner").tag("Signer")
                    Text("👁 Nur lesen").tag("Viewer")
                }
                Button("+ Hinzufügen") { Task { await addParticipant() } }
                    .disabled(newEmail.isEmpty || newName.isEmpty)
            }
            if !participants.isEmpty {
                Section("Hinzugefügt") {
                    ForEach(participants) { p in
                        HStack {
                            Image(systemName: p.role == "Signer" ? "signature" : "eye")
                                .foregroundStyle(Theme.tungstenBlue)
                            VStack(alignment: .leading) {
                                Text(p.name).font(.body.weight(.medium))
                                Text(p.email).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
            if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
        }
    }

    /// v1.10.66: PDFs aus ALLEN Bibliotheken sammeln (Personal, Public,
    /// alle Gruppen) — Marcus's Bug: "keine PDFs vorhanden" trat auf wenn
    /// die relevanten PDFs im Public- oder Gruppen-Scope lagen und Personal
    /// leer war. Wir zeigen jetzt jedes lesbare PDF quer über die Scopes.
    /// v1.10.147: Server liefert unter /browse/scopes nur noch Personal +
    /// Public (v1.10.102-Änderung). Gruppen müssen wir separat via
    /// listShareableGroups holen und pro Group-ID browsen — sonst waren
    /// PDFs in Gruppen-Ordnern unsichtbar für die Wizard-Auswahl.
    private func loadPersonalFiles() async {
        guard let api = auth.api else { return }
        do {
            scopes = try await api.scopes()
            var collected: [FileItem] = []
            for tile in scopes {
                do {
                    let r = try await api.browse(scope: tile.scope, groupId: tile.groupId, path: nil)
                    collected.append(contentsOf: r.files)
                } catch { /* stille Fehler pro Scope — nicht den ganzen Sheet blockieren */ }
            }
            // v1.10.147: Gruppen zusätzlich abklappern, damit Bestands-PDFs
            // aus Gruppen-Ordnern signierbar bleiben (Server-Kommentar in
            // BrowseController.MobileScopes bestätigt diese Absicht).
            if let groups = try? await api.listShareableGroups() {
                for g in groups {
                    do {
                        let r = try await api.browse(scope: "Group", groupId: g.id, path: nil)
                        collected.append(contentsOf: r.files)
                    } catch { /* stille Fehler pro Gruppe — irrelevant */ }
                }
            }
            // Duplikate (dieselbe File-ID kann via Pin in mehreren Scopes
            // erscheinen) einmalig zeigen.
            var seen = Set<UUID>()
            files = collected.filter { seen.insert($0.id).inserted }
        } catch let ex {
            // v1.10.79: äußerer Fehler (scopes()) war komplett silent —
            // User sah leere Datei-Liste ohne Ursache. Jetzt via error-
            // State sichtbar, damit man wenigstens weiß dass es hakt.
            error = ex.localizedDescription
        }
    }

    private func createDraft() async {
        guard let api = auth.api, let fid = pickedFileId else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            let r = try await api.createSignatureRequest(
                sourceFileId: fid,
                title: title.isEmpty ? nil : title,
                message: message.isEmpty ? nil : message,
                deliveryOrder: deliveryOrder,
                deadline: useDeadline ? deadline : nil)
            requestId = r.id
            // v1.10.71: Adressbuch für stepTwo vorladen — Autocomplete für Empfänger.
            if let contacts = try? await api.listContacts() {
                contactSuggestions = contacts
            }
            step = 2
        } catch let ex { error = ex.localizedDescription }
    }

    private func addParticipant() async {
        guard let api = auth.api, let rid = requestId else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            let pid = try await api.addSignatureParticipant(rid, email: newEmail, name: newName,
                role: newRole, order: participants.count)
            if newRole == "Signer" {
                _ = try await api.addSignatureField(rid, participantId: pid, type: "Signature",
                    page: 1, anchor: "BottomCenter")
            }
            participants.append(Participant(email: newEmail, name: newName, role: newRole, serverId: pid))
            newEmail = ""; newName = ""
        } catch let ex { error = ex.localizedDescription }
    }

    private func send() async {
        guard let api = auth.api, let rid = requestId else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            _ = try await api.sendSignatureRequest(rid)
            onDone()
        } catch let ex { error = ex.localizedDescription }
    }
}
