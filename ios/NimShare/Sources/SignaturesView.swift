import SwiftUI

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
                List {
                    ForEach(items) { r in
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
            .task { if scopes.isEmpty { await loadPersonalFiles() } }
            .overlay { if busy { ProgressView() } }
        }
    }

    @ViewBuilder
    private var stepOne: some View {
        Form {
            Section("PDF wählen") {
                if files.isEmpty {
                    Text("Keine PDFs in deiner Personal-Ablage.").foregroundStyle(.secondary)
                } else {
                    Picker("Datei", selection: $pickedFileId) {
                        Text("—").tag(UUID?.none)
                        ForEach(files.filter { $0.contentType.contains("pdf") }) { f in
                            Text(f.name).tag(Optional(f.id))
                        }
                    }
                }
            }
            Section("Titel & Nachricht") {
                TextField("Titel (Standard: Dateiname)", text: $title)
                TextField("Nachricht an die Empfänger", text: $message, axis: .vertical).lineLimit(2...5)
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

    private func loadPersonalFiles() async {
        guard let api = auth.api else { return }
        do {
            scopes = try await api.scopes()
            let personal = try await api.browse(scope: "Personal", groupId: nil, path: nil)
            files = personal.files
        } catch { }
    }

    private func createDraft() async {
        guard let api = auth.api, let fid = pickedFileId else { return }
        busy = true; error = nil; defer { busy = false }
        do {
            let r = try await api.createSignatureRequest(sourceFileId: fid,
                title: title.isEmpty ? nil : title,
                message: message.isEmpty ? nil : message)
            requestId = r.id
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
