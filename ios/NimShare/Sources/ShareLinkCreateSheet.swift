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
    @State private var useExpiry = false
    @State private var expiryDate = Date().addingTimeInterval(60*60*24*7)
    @State private var message = ""
    @State private var notifyOnAccess = false

    @State private var busy = false
    @State private var error: String?
    @State private var result: ShareLinkDto?

    var body: some View {
        NavigationStack {
            if let r = result {
                resultView(r)
            } else {
                formView
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
            Section("Passwortschutz (optional)") {
                SecureField("Passwort", text: $password)
                    .textContentType(.newPassword)
            }
            Section("Limits") {
                TextField("Max. Downloads (leer = unbegrenzt)", text: $maxDownloadsText)
                    .keyboardType(.numberPad)
                Toggle("Ablaufdatum setzen", isOn: $useExpiry)
                if useExpiry {
                    DatePicker("Läuft ab am", selection: $expiryDate, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                }
            }
            Section("Nachricht (optional)") {
                TextField("Kurze Nachricht für den Empfänger", text: $message, axis: .vertical)
                    .lineLimit(2...5)
            }
            Section {
                Toggle("Benachrichtigen wenn Link geöffnet wird", isOn: $notifyOnAccess)
            }
            if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
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
            Text(r.url).font(.footnote.monospaced()).foregroundStyle(.secondary)
                .padding(.horizontal, 20).multilineTextAlignment(.center)
            HStack(spacing: 12) {
                Button {
                    UIPasteboard.general.string = r.url
                } label: {
                    Label("Kopieren", systemImage: "doc.on.doc")
                }.buttonStyle(.borderedProminent).tint(Theme.tungstenBlue)
                if let u = URL(string: r.url) {
                    ShareLink(item: u) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }.buttonStyle(.bordered).tint(Theme.tungstenBlue)
                }
            }
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
                expiresAt: useExpiry ? expiryDate : nil,
                message: message.isEmpty ? nil : message,
                notifyOnAccess: notifyOnAccess
            )
            result = link
        } catch let ex { error = ex.localizedDescription }
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
    @State private var useExpiry = false
    @State private var expiryDate = Date().addingTimeInterval(60*60*24*7)
    @State private var message = ""
    @State private var notifyOnUpload = true

    @State private var busy = false
    @State private var error: String?
    @State private var result: NimShareAPI.UploadRequestResult?

    var body: some View {
        NavigationStack {
            if let r = result {
                resultView(r)
            } else {
                formView
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
            Section("Passwortschutz (optional)") {
                SecureField("Passwort", text: $password).textContentType(.newPassword)
            }
            Section("Limits") {
                TextField("Max. Uploads (leer = unbegrenzt)", text: $maxUploadsText)
                    .keyboardType(.numberPad)
                Toggle("Ablaufdatum setzen", isOn: $useExpiry)
                if useExpiry {
                    DatePicker("Läuft ab am", selection: $expiryDate, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                }
            }
            Section("Nachricht an den Empfänger") {
                TextField("Was soll hochgeladen werden?", text: $message, axis: .vertical)
                    .lineLimit(2...5)
            }
            Section {
                Toggle("Bei Upload benachrichtigen", isOn: $notifyOnUpload)
            }
            if let e = error { Section { Text(e).foregroundStyle(Theme.warnRed) } }
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
            Text(r.url).font(.footnote.monospaced()).foregroundStyle(.secondary)
                .padding(.horizontal, 20).multilineTextAlignment(.center)
            HStack(spacing: 12) {
                Button { UIPasteboard.general.string = r.url } label: {
                    Label("Kopieren", systemImage: "doc.on.doc")
                }.buttonStyle(.borderedProminent).tint(Theme.tungstenBlue)
                if let u = URL(string: r.url) {
                    ShareLink(item: u) { Label("Teilen", systemImage: "square.and.arrow.up") }
                        .buttonStyle(.bordered).tint(Theme.tungstenBlue)
                }
            }
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
                expiresAt: useExpiry ? expiryDate : nil,
                message: message.isEmpty ? nil : message,
                targetFolder: (targetFolderName?.isEmpty == false) ? targetFolderName! : "Received",
                notifyOnUpload: notifyOnUpload
            )
        } catch let ex { error = ex.localizedDescription }
    }
}
