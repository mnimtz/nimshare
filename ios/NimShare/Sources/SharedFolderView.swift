import SwiftUI

/// v1.10.148 — Bug #7: Ansicht für einen freigegebenen Ordner (Tap aus
/// „Für mich freigegeben"). Lädt Inhalte über den neuen
/// GET /api/v1/folders/{id}/browse-Endpoint, statt Scope+Path zu raten.
/// Read-only Anzeige: Sub-Ordner navigierbar rekursiv, Files öffnen den
/// bestehenden FilePreviewView.
struct SharedFolderView: View {
    @EnvironmentObject var auth: AuthStore
    let folderId: UUID
    let initialTitle: String

    @State private var payload: NimShareAPI.FolderBrowseResponse?
    @State private var loading = true
    @State private var error: String?
    @State private var previewFile: FileItem?

    var body: some View {
        Group {
            if loading && payload == nil {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, payload == nil {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                    Button("Erneut versuchen") { Task { await load() } }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let p = payload {
                if p.subfolders.isEmpty && p.files.isEmpty {
                    ContentUnavailableView("Ordner ist leer", systemImage: "folder",
                        description: Text("Hier gibt es aktuell nichts."))
                } else {
                    List {
                        if !p.subfolders.isEmpty {
                            Section("Ordner") {
                                ForEach(p.subfolders) { sub in
                                    NavigationLink {
                                        SharedFolderView(folderId: sub.id, initialTitle: sub.name)
                                    } label: {
                                        Label(sub.name, systemImage: "folder.fill")
                                            .foregroundStyle(Color.orange)
                                    }
                                }
                            }
                        }
                        if !p.files.isEmpty {
                            Section("Dateien") {
                                ForEach(p.files) { f in
                                    Button {
                                        previewFile = FileItem(
                                            id: f.id, name: f.name, sizeBytes: f.sizeBytes,
                                            contentType: f.contentType, createdAt: f.createdAt,
                                            ownerName: nil, aiTags: nil, aiRiskFlag: nil)
                                    } label: {
                                        HStack {
                                            FileFormatBadge(name: f.name, size: 28)
                                            VStack(alignment: .leading, spacing: 2) {
                                                Text(f.name).foregroundStyle(.primary)
                                                Text(ByteCountFormatter.string(fromByteCount: f.sizeBytes, countStyle: .file))
                                                    .font(.caption).foregroundStyle(.secondary)
                                            }
                                            Spacer()
                                        }
                                        .contentShape(Rectangle())
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle(payload?.name ?? initialTitle)
        .task { await load() }
        .refreshable { await load() }
        .sheet(item: $previewFile) { f in NavigationStack { FilePreviewView(file: f) } }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do { payload = try await api.browseFolder(folderId) }
        catch let ex { error = ex.localizedDescription }
    }
}
