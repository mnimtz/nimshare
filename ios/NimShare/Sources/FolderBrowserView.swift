import SwiftUI

struct FolderBrowserView: View {
    @EnvironmentObject var auth: AuthStore
    let scope: String
    let groupId: UUID?
    let path: String
    let title: String

    @State private var data: BrowseResponse?
    @State private var loading = true
    @State private var error: String?
    @State private var previewFile: FileItem?
    @State private var directShareTarget: DirectShareSheet.Target?
    @State private var directShareName: String = ""
    // v1.10.71: statt Quick-Sheet mit URL — vollwertige Create-Sheets
    // mit Slug/Passwort/Ablauf/etc. Web-Parity.
    @State private var shareTarget: ShareLinkCreateSheet.Target?
    @State private var shareItemName: String = ""
    @State private var showUploadRequest = false
    // v1.10.72: Multi-Selektion + Bulk-Actions (Web-Parity).
    @State private var editMode: EditMode = .inactive
    @State private var selection = Set<UUID>()
    @State private var bulkPickerOp: BulkOp?
    enum BulkOp: Identifiable {
        case move, copy
        var id: String { self == .move ? "m" : "c" }
    }
    // v1.10.66: Ergebnis eines schnellen Share-Link/Upload-Request Create
    // — wird als Bottom-Sheet mit URL + Copy/Teilen gezeigt.
    @State private var linkResult: LinkResult?
    @State private var busy = false
    // v1.10.70: Rename + Folder-Picker States. Alert für Rename (kurzer
    // Text-Input reicht), Sheet für Move/Copy-Target-Auswahl.
    @State private var renaming: (kind: String, id: UUID, current: String)?
    @State private var renameText: String = ""
    @State private var newFolderParent: UUID?
    @State private var newFolderName: String = ""
    @State private var pickerOp: FolderPickerOp?

    enum FolderPickerOp: Identifiable {
        case move(fileId: UUID, name: String)
        case copy(fileId: UUID, name: String)
        // v1.10.113: Ordner verschieben/kopieren (Web-Parität).
        case moveFolder(folderId: UUID, name: String)
        case copyFolder(folderId: UUID, name: String)
        var id: String {
            switch self {
            case .move(let f, _): return "m-\(f)"
            case .copy(let f, _): return "c-\(f)"
            case .moveFolder(let f, _): return "mf-\(f)"
            case .copyFolder(let f, _): return "cf-\(f)"
            }
        }
    }
    // v1.10.79: Delete-Confirmation. Kein One-Tap-Datenverlust mehr.
    @State private var pendingDelete: (id: UUID, name: String)?
    // v1.10.113: separate Ordner-Löschbestätigung (rekursiv).
    @State private var pendingFolderDelete: (id: UUID, name: String)?
    // v1.10.113: Upload-Anfrage für einen bestimmten Ordner.
    @State private var uploadReqFolderName: String?
    // v1.10.104: Berechtigungen-Sheet (Public „Windows-ACL"). Nur für
    // Public-Scope-Ordner sinnvoll — Long-Press blendet den Eintrag
    // in Personal/Group aus.
    struct PermissionsTargetRef: Identifiable {
        let id: UUID
        let name: String
    }
    @State private var permissionsTarget: PermissionsTargetRef?

    struct LinkResult: Identifiable {
        let id = UUID()
        let title: String
        let url: String
    }

    var body: some View {
        Group {
            if loading && data == nil {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, data == nil {
                errorView(e)
            } else if let d = data {
                list(d)
            }
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            // v1.10.72: Auswahl-Modus (Bulk-Selection wie im Web).
            ToolbarItem(placement: .topBarTrailing) {
                if editMode == .active {
                    Button("Fertig") {
                        editMode = .inactive
                        selection.removeAll()
                    }
                } else {
                    Menu {
                        Button {
                            editMode = .active
                        } label: {
                            Label("Auswahl", systemImage: "checkmark.circle")
                        }
                        Divider()
                        if let cur = data?.currentFolderId {
                            Button {
                                newFolderParent = cur
                                newFolderName = ""
                            } label: {
                                Label("Neuer Unterordner", systemImage: "folder.badge.plus")
                            }
                        }
                        Button {
                            showUploadRequest = true
                        } label: {
                            Label("Upload anfordern", systemImage: "square.and.arrow.down")
                        }
                    } label: {
                        Image(systemName: "plus.circle")
                    }
                    .disabled(busy)
                }
            }
        }
        .environment(\.editMode, $editMode)
        .safeAreaInset(edge: .bottom) {
            if editMode == .active && !selection.isEmpty {
                bulkBar
            }
        }
        .task(id: path) { await load() }
        .refreshable { await load() }
        .sheet(item: $previewFile) { file in
            NavigationStack { FilePreviewView(file: file) }
        }
        .sheet(item: $directShareTarget) { target in
            DirectShareSheet(target: target, itemName: directShareName)
        }
        // v1.10.104: PermissionsSheet für Public-Ordner (Privacy-Toggle + ACL)
        .sheet(item: $permissionsTarget) { ref in
            PermissionsSheet(folderId: ref.id, folderName: ref.name)
        }
        .sheet(item: $linkResult) { r in
            NavigationStack {
                LinkResultSheet(title: r.title, url: r.url)
            }.presentationDetents([.medium])
        }
        // v1.10.71: vollwertige Freigabelink-Create-Sheet
        .sheet(item: $shareTarget) { t in
            ShareLinkCreateSheet(target: t, itemName: shareItemName)
        }
        .sheet(isPresented: $showUploadRequest, onDismiss: { uploadReqFolderName = nil }) {
            UploadRequestCreateSheet(targetFolderName: uploadReqFolderName)
        }
        // v1.10.72: Bulk-Move/Copy — dasselbe FolderPickerSheet wie
        // Single-Item, aber loopt über die Selection.
        .sheet(item: $bulkPickerOp) { op in
            FolderPickerSheet(title: op == .move ? "\(selection.count) verschieben nach" : "\(selection.count) kopieren nach") { targetId, _ in
                Task { await bulkMoveOrCopy(op, targetId: targetId) }
            }
        }
        // v1.10.70: Move/Copy → Folder-Picker-Sheet öffnen
        .sheet(item: $pickerOp) { op in
            switch op {
            case .move(let fid, let name):
                FolderPickerSheet(title: "\"\(name)\" verschieben nach") { targetId, targetPath in
                    Task { await performMove(fileId: fid, targetId: targetId, targetPath: targetPath) }
                }
            case .copy(let fid, let name):
                FolderPickerSheet(title: "\"\(name)\" kopieren nach") { targetId, targetPath in
                    Task { await performCopy(fileId: fid, targetId: targetId, targetPath: targetPath) }
                }
            case .moveFolder(let fid, let name):
                FolderPickerSheet(title: "\"\(name)\" verschieben nach") { targetId, targetPath in
                    Task { await performMoveFolder(folderId: fid, targetId: targetId, targetPath: targetPath) }
                }
            case .copyFolder(let fid, let name):
                FolderPickerSheet(title: "\"\(name)\" kopieren nach") { targetId, targetPath in
                    Task { await performCopyFolder(folderId: fid, targetId: targetId, targetPath: targetPath) }
                }
            }
        }
        // v1.10.113: Ordner-Löschbestätigung (rekursiv → Papierkorb).
        .alert("Ordner löschen?", isPresented: Binding(
            get: { pendingFolderDelete != nil },
            set: { if !$0 { pendingFolderDelete = nil } }
        )) {
            Button("Abbrechen", role: .cancel) { pendingFolderDelete = nil }
            Button("In Papierkorb", role: .destructive) {
                if let d = pendingFolderDelete { Task { await deleteFolder(d.id) } }
                pendingFolderDelete = nil
            }
        } message: {
            Text(#"„\#(pendingFolderDelete?.name ?? "")" wird mit seinem gesamten Inhalt in den Papierkorb verschoben."#)
        }
        // v1.10.70: Rename-Alert (Datei ODER Ordner)
        .alert("Umbenennen", isPresented: Binding(
            get: { renaming != nil },
            set: { if !$0 { renaming = nil } }
        )) {
            TextField("Name", text: $renameText)
            Button("Abbrechen", role: .cancel) { renaming = nil }
            Button("OK") { Task { await performRename() } }
        } message: {
            Text(renaming.map { "Neuer Name für \"\($0.current)\"" } ?? "")
        }
        // v1.10.70: Neuer-Unterordner-Alert
        .alert("Neuer Ordner", isPresented: Binding(
            get: { newFolderParent != nil },
            set: { if !$0 { newFolderParent = nil } }
        )) {
            TextField("Ordnername", text: $newFolderName)
            Button("Abbrechen", role: .cancel) { newFolderParent = nil }
            Button("Anlegen") { Task { await performCreateFolder() } }
        } message: {
            Text("Name für den neuen Unterordner")
        }
        .overlay { if busy { ProgressView().padding(24).background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12)) } }
        // v1.10.79: Delete-Confirmation für Datei-Löschen. Kein One-Tap-
        // Datenverlust mehr aus Kontext-Menü oder Swipe.
        .confirmationDialog(
            pendingDelete.map { "\"\($0.name)\" in Papierkorb?" } ?? "",
            isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
            titleVisibility: .visible
        ) {
            if let d = pendingDelete {
                Button("Löschen", role: .destructive) {
                    let id = d.id; pendingDelete = nil
                    Task { await deleteFile(id) }
                }
                Button("Abbrechen", role: .cancel) { pendingDelete = nil }
            }
        } message: {
            Text("Die Datei landet im Papierkorb und kann von dort wiederhergestellt werden.")
        }
        // v1.10.79: Aktion-Fehler-Alert. Vorher wurden Fehler aus Move/Copy/
        // Rename/Delete nur in `error` geschrieben, aber nur im initial-
        // Load-State (`data == nil`) angezeigt. Nach erfolgreichem Load
        // waren alle Folgefehler unsichtbar — User dachte alles OK, obwohl
        // z.B. „5 OK, 3 fehlgeschlagen" nirgendwo auftauchte.
        .alert("Fehler", isPresented: Binding(
            get: { error != nil && data != nil },
            set: { if !$0 { error = nil } }
        )) {
            Button("OK", role: .cancel) { error = nil }
        } message: {
            Text(error ?? "")
        }
    }

    private func list(_ d: BrowseResponse) -> some View {
        // v1.10.72: List mit selection-Set → Multi-Select im EditMode.
        // Nur Files sind selektierbar (Folders navigierbar; bulk-ops
        // operieren nur auf Files — analog Web).
        List(selection: $selection) {
            if !d.subfolders.isEmpty {
                Section("Ordner") {
                    ForEach(d.subfolders) { f in
                        NavigationLink {
                            FolderBrowserView(
                                scope: scope, groupId: groupId,
                                path: joinPath(path, f.name),
                                title: f.name
                            )
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: "folder.fill")
                                    .foregroundStyle(Theme.tungstenBlue)
                                    .frame(width: 24)
                                Text(f.name)
                            }
                        }
                        .contextMenu {
                            Button {
                                shareItemName = f.name
                                shareTarget = .folder(f.id)
                            } label: { Label("Freigabelink erstellen", systemImage: "link.badge.plus") }
                            // v1.10.105: EIN Berechtigungen-Eintrag pro Scope statt
                            // zwei. Vorher: für Public wurden „Berechtigungen…"
                            // (DirectShareSheet) UND „🔒 Windows-Berechtigungen"
                            // (PermissionsSheet mit Privacy-Toggle) parallel gezeigt
                            // — beide auf dieselbe Backend-Tabelle. Konsolidiert:
                            //   • Public → PermissionsSheet (Grants + Privacy-Toggle)
                            //   • Personal/Group → DirectShareSheet (nur Grants,
                            //     Privacy-Konzept existiert dort nicht)
                            Button {
                                if scope.lowercased() == "public" {
                                    permissionsTarget = PermissionsTargetRef(id: f.id, name: f.name)
                                } else {
                                    directShareName = f.name
                                    directShareTarget = .folder(f.id)
                                }
                            } label: {
                                Label("Berechtigungen…",
                                      systemImage: scope.lowercased() == "public"
                                        ? "lock.shield" : "person.crop.circle.badge.plus")
                            }
                            // v1.10.113: Upload anfordern für DIESEN Ordner (Web-Parität).
                            Button {
                                uploadReqFolderName = f.name
                                showUploadRequest = true
                            } label: { Label("Upload anfordern", systemImage: "tray.and.arrow.down") }
                            Button {
                                newFolderParent = f.id
                                newFolderName = ""
                            } label: { Label("Neuer Unterordner", systemImage: "folder.badge.plus") }
                            Button {
                                renaming = ("folder", f.id, f.name)
                                renameText = f.name
                            } label: { Label("Umbenennen", systemImage: "pencil") }
                            // v1.10.113: Ordner verschieben/kopieren (Backend v1.10.110).
                            Button {
                                pickerOp = .moveFolder(folderId: f.id, name: f.name)
                            } label: { Label("Verschieben nach…", systemImage: "folder") }
                            Button {
                                pickerOp = .copyFolder(folderId: f.id, name: f.name)
                            } label: { Label("Kopieren nach…", systemImage: "doc.on.doc") }
                            Button { Task { await toggleFav(folderId: f.id) } } label: {
                                Label("Favorit", systemImage: "star")
                            }
                            Button(role: .destructive) { pendingFolderDelete = (f.id, f.name) } label: {
                                Label("In Papierkorb", systemImage: "trash")
                            }
                        }
                    }
                }
            }
            if !d.files.isEmpty {
                Section("Dateien") {
                    ForEach(d.files) { f in
                        Group {
                            if editMode == .active {
                                // Im Auswahl-Modus: Row selbst als
                                // selection-Target — Tap toggelt Checkmark.
                                FileRowView(file: f)
                            } else {
                                Button { previewFile = f } label: {
                                    FileRowView(file: f)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                        .contextMenu {
                            Button { previewFile = f } label: { Label("Vorschau", systemImage: "eye") }
                            Button {
                                Task { await downloadFile(f) }
                            } label: { Label("Herunterladen", systemImage: "arrow.down.circle") }
                            Button {
                                shareItemName = f.name
                                shareTarget = .file(f.id)
                            } label: { Label("Freigabelink erstellen", systemImage: "link.badge.plus") }
                            Button {
                                directShareName = f.name
                                directShareTarget = .file(f.id)
                            } label: { Label("Berechtigungen…", systemImage: "person.crop.circle.badge.plus") }
                            Button {
                                pickerOp = .move(fileId: f.id, name: f.name)
                            } label: { Label("Verschieben nach…", systemImage: "folder") }
                            Button {
                                pickerOp = .copy(fileId: f.id, name: f.name)
                            } label: { Label("Kopieren nach…", systemImage: "doc.on.doc") }
                            Button {
                                renaming = ("file", f.id, f.name)
                                renameText = f.name
                            } label: { Label("Umbenennen", systemImage: "pencil") }
                            Button { Task { await toggleFav(fileId: f.id) } } label: {
                                Label("Favorit", systemImage: "star")
                            }
                            Button(role: .destructive) { pendingDelete = (f.id, f.name) } label: {
                                Label("In Papierkorb", systemImage: "trash")
                            }
                        }
                        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                            Button(role: .destructive) { pendingDelete = (f.id, f.name) } label: {
                                Label("Löschen", systemImage: "trash")
                            }
                            Button { Task { await toggleFav(fileId: f.id) } } label: {
                                Label("Fav", systemImage: "star")
                            }.tint(.yellow)
                            Button {
                                shareItemName = f.name
                                shareTarget = .file(f.id)
                            } label: {
                                Label("Teilen", systemImage: "link.badge.plus")
                            }.tint(Theme.tungstenBlue)
                        }
                    }
                }
            }
            if d.subfolders.isEmpty && d.files.isEmpty {
                ContentUnavailableView("Leer", systemImage: "tray", description: Text("Dieser Ordner ist leer."))
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
            }
        }
    }

    private func joinPath(_ base: String, _ segment: String) -> String {
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "/")
        let escaped = segment.addingPercentEncoding(withAllowedCharacters: allowed) ?? segment
        return base.isEmpty ? escaped : base + "/" + escaped
    }

    private func errorView(_ e: String) -> some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
            Text(e).multilineTextAlignment(.center).padding(.horizontal)
            Button("Erneut versuchen") { Task { await load() } }
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do {
            data = try await api.browse(scope: scope, groupId: groupId, path: path.isEmpty ? nil : path)
        } catch let e as ApiError {
            error = e.localizedDescription
            if case .notAuthorized = e { auth.signOut() }
        } catch let ex { error = ex.localizedDescription }
    }

    private func toggleFav(fileId: UUID? = nil, folderId: UUID? = nil) async {
        guard let api = auth.api else { return }
        do { _ = try await api.toggleFavorite(fileId: fileId, folderId: folderId) }
        catch let ex { error = ex.localizedDescription }
    }

    private func deleteFile(_ id: UUID) async {
        guard let api = auth.api else { return }
        do {
            try await api.deleteFile(id)
            await load()
        } catch let ex { error = ex.localizedDescription }
    }

    // v1.10.113: Ordner-Operationen (Web-Parität).
    private func deleteFolder(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.deleteFolder(id: id, force: true); await load() }
        catch let ex { error = ex.localizedDescription }
    }
    private func performMoveFolder(folderId: UUID, targetId: UUID, targetPath: String) async {
        guard let api = auth.api else { return }
        do { try await api.moveFolder(id: folderId, targetFolderId: targetId); await load() }
        catch let ex { error = ex.localizedDescription }
    }
    private func performCopyFolder(folderId: UUID, targetId: UUID, targetPath: String) async {
        guard let api = auth.api else { return }
        do { try await api.copyFolder(id: folderId, targetFolderId: targetId); await load() }
        catch let ex { error = ex.localizedDescription }
    }

    // v1.10.66: Share-Link mit Default-Optionen erstellen. Der User sieht
    // sofort die URL im Bottom-Sheet und kann kopieren/teilen.
    private func createShareLink(fileId: UUID? = nil, folderId: UUID? = nil, name: String) async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            let link = try await api.createShareLink(fileId: fileId, folderId: folderId)
            linkResult = LinkResult(title: "Freigabelink: \(name)", url: link.url)
        } catch let ex { error = ex.localizedDescription }
    }

    private func createUploadRequest() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            let r = try await api.createUploadRequest(message: nil)
            linkResult = LinkResult(title: "Upload-Anforderung", url: r.url)
        } catch let ex { error = ex.localizedDescription }
    }

    /// v1.10.72: Fixierte Bottom-Bar mit Bulk-Actions — sichtbar wenn
    /// Auswahl-Modus aktiv UND mind. eine Datei markiert ist.
    private var bulkBar: some View {
        HStack(spacing: 16) {
            Text("\(selection.count) markiert").font(.footnote).foregroundStyle(.secondary)
            Spacer()
            Button {
                bulkPickerOp = .move
            } label: { Image(systemName: "folder") }
                .disabled(busy)
            Button {
                bulkPickerOp = .copy
            } label: { Image(systemName: "doc.on.doc") }
                .disabled(busy)
            Button {
                Task { await bulkDownloadZip() }
            } label: { Image(systemName: "arrow.down.circle") }
                .disabled(busy)
            Button(role: .destructive) {
                Task { await bulkDelete() }
            } label: { Image(systemName: "trash") }
                .disabled(busy)
        }
        .padding(.horizontal, 16).padding(.vertical, 10)
        .background(.thinMaterial)
    }

    private func bulkDelete() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            try await api.bulkDeleteFiles(Array(selection))
            selection.removeAll()
            editMode = .inactive
            await load()
        } catch let ex { error = ex.localizedDescription }
    }

    private func bulkDownloadZip() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            let (bytes, name) = try await api.bulkZipFiles(Array(selection))
            // v1.10.79: TmpFile + iPad-safe Share-Sheet via zentralem Helper
            let dest = TmpFile.destinationURL(for: name)
            try bytes.write(to: dest)
            selection.removeAll()
            editMode = .inactive
            await MainActor.run { TmpFile.presentShareSheet(for: [dest]) }
        } catch let ex { error = ex.localizedDescription }
    }

    private func bulkMoveOrCopy(_ op: BulkOp, targetId: UUID) async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        var ok = 0, fail = 0
        for id in selection {
            do {
                switch op {
                case .move: try await api.moveFile(id: id, targetFolderId: targetId); ok += 1
                case .copy: try await api.copyFile(id: id, targetFolderId: targetId); ok += 1
                }
            } catch { fail += 1 }
        }
        if fail > 0 { error = "\(ok) OK, \(fail) fehlgeschlagen" }
        selection.removeAll()
        editMode = .inactive
        await load()
    }

    // v1.10.70: Move/Copy/Rename/CreateFolder Perform-Helpers.
    // Alle laufen mit dem gleichen Muster: busy-Overlay an, API-Call,
    // Toast bei OK / Alert bei Fehler, Reload.
    private func performMove(fileId: UUID, targetId: UUID, targetPath: String) async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            try await api.moveFile(id: fileId, targetFolderId: targetId)
            await load()
        } catch let ex { error = ex.localizedDescription }
    }
    private func performCopy(fileId: UUID, targetId: UUID, targetPath: String) async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            try await api.copyFile(id: fileId, targetFolderId: targetId)
            // v1.10.79: fehlender load() — vorher zeigte der Browser die
            // Kopie nur nach manuellem Refresh. Bei Kopie in denselben
            // Ordner (Duplikat) blieb es scheinbar wirkungslos.
            await load()
        } catch let ex { error = ex.localizedDescription }
    }
    private func performRename() async {
        guard let api = auth.api, let r = renaming else { return }
        let newName = renameText.trimmingCharacters(in: .whitespaces)
        renaming = nil
        if newName.isEmpty || newName == r.current { return }
        busy = true; defer { busy = false }
        do {
            if r.kind == "folder" { try await api.renameFolder(id: r.id, newName: newName) }
            else { try await api.renameFile(id: r.id, newName: newName) }
            await load()
        } catch let ex { error = ex.localizedDescription }
    }
    private func performCreateFolder() async {
        guard let api = auth.api, let parent = newFolderParent else { return }
        let name = newFolderName.trimmingCharacters(in: .whitespaces)
        newFolderParent = nil
        if name.isEmpty { return }
        busy = true; defer { busy = false }
        do {
            _ = try await api.createFolder(parentId: parent, name: name)
            await load()
        } catch let ex { error = ex.localizedDescription }
    }

    // Datei-Download via preview-url (Content-Disposition=attachment) →
    // System-Share-Sheet mit dem lokalen Temp-File. Der User kann direkt
    // "In Dateien sichern", "Auf Kamerarolle sichern" etc. wählen.
    private func downloadFile(_ f: FileItem) async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            let r = try await api.previewUrl(fileId: f.id)
            guard let src = URL(string: r.url) else { throw ApiError.network("Bad URL") }
            let (tmp, _) = try await URLSession.shared.download(from: src)
            // v1.10.79: TmpFile + iPad-safe Share-Sheet via zentralem Helper
            let dest = TmpFile.destinationURL(for: f.name)
            try FileManager.default.moveItem(at: tmp, to: dest)
            await MainActor.run { TmpFile.presentShareSheet(for: [dest]) }
        } catch let ex { error = ex.localizedDescription }
    }
}

/// v1.10.66: Bottom-Sheet mit URL + Copy/Teilen. Reused für Share-Links
/// und Upload-Requests — beide zeigen dasselbe Ergebnis-Format.
struct LinkResultSheet: View {
    @Environment(\.dismiss) private var dismiss
    let title: String
    let url: String

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "link.circle.fill")
                .font(.system(size: 44))
                .foregroundStyle(Theme.tungstenBlue)
                .padding(.top, 20)
            Text(title).font(.headline).multilineTextAlignment(.center)
            TextField("", text: .constant(url))
                .textFieldStyle(.roundedBorder)
                .disabled(true)
                .padding(.horizontal, 20)
            HStack(spacing: 12) {
                Button {
                    UIPasteboard.general.string = url
                } label: {
                    Label("Kopieren", systemImage: "doc.on.doc")
                }
                .buttonStyle(.borderedProminent)
                .tint(Theme.tungstenBlue)
                if let u = URL(string: url) {
                    ShareLink(item: u) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }
                    .buttonStyle(.bordered)
                    .tint(Theme.tungstenBlue)
                }
            }
            Spacer()
        }
        .padding()
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button("Fertig") { dismiss() }
            }
        }
    }
}

extension DirectShareSheet.Target: Identifiable {
    var id: String {
        switch self {
        case .file(let id): return "f-\(id)"
        case .folder(let id): return "d-\(id)"
        }
    }
}

struct FileRowView: View {
    let file: FileItem

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: file.iconName)
                .foregroundStyle(Theme.tungstenBlue)
                .frame(width: 24, alignment: .center)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 4) {
                Text(file.name).lineLimit(2)
                HStack(spacing: 8) {
                    Text(byteCountFormatter.string(fromByteCount: file.sizeBytes))
                        .font(.caption).foregroundStyle(.secondary)
                    if let owner = file.ownerName {
                        Text("· " + owner).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                if !file.tags.isEmpty || file.aiRiskFlag != nil {
                    HStack(spacing: 6) {
                        if let risk = file.aiRiskFlag {
                            Chip(text: "⚠ " + risk, color: Theme.warnRed, bg: Theme.warnRed.opacity(0.12))
                        }
                        ForEach(file.tags.prefix(3), id: \.self) { tag in
                            Chip(text: tag, color: Theme.tungstenBlue, bg: Theme.aiBlueTintBg)
                        }
                    }
                }
            }
        }
        .padding(.vertical, 2)
    }
}

struct Chip: View {
    let text: String
    let color: Color
    let bg: Color
    var body: some View {
        Text(text)
            .font(.caption2.weight(.medium))
            .padding(.horizontal, 6).padding(.vertical, 2)
            .foregroundStyle(color)
            .background(bg)
            .clipShape(RoundedRectangle(cornerRadius: 4))
    }
}

private let byteCountFormatter: ByteCountFormatter = {
    let f = ByteCountFormatter()
    f.countStyle = .file
    return f
}()
