import SwiftUI
import QuickLook

struct FilePreviewView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let file: FileItem

    @State private var localURL: URL?
    @State private var busy = true
    @State private var error: String?
    // v1.10.82: App-Store-Blocker Apple 1.2 — Datei-Melden von hier aus.
    @State private var showReport = false
    // v1.10.88: File-Lock-Status + Actions (iOS-Parität zum Web-Lock)
    @State private var lockStatus: NimShareAPI.FileLockStatus?

    var body: some View {
        Group {
            if busy {
                ProgressView("Lädt…").frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let url = localURL {
                QLQuickLookView(url: url)
                    .ignoresSafeArea(edges: .bottom)
            } else if let e = error {
                VStack(spacing: 12) {
                    Image(systemName: "doc.questionmark").font(.largeTitle)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(file.name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Button("Schließen") { dismiss() }
            }
            ToolbarItem(placement: .topBarTrailing) {
                HStack {
                    // v1.10.72: Versionen-History
                    NavigationLink { FileVersionsView(fileId: file.id, fileName: file.name) } label: {
                        Image(systemName: "clock.arrow.circlepath")
                    }
                    if let url = localURL {
                        ShareLink(item: url) { Image(systemName: "square.and.arrow.up") }
                    }
                    // v1.10.82: Menu mit Melden — App-Store-Blocker Apple 1.2
                    // v1.10.88: File-Lock-Actions unter demselben Menu.
                    Menu {
                        if let s = lockStatus, s.locked {
                            Label("Gesperrt von \(s.byUserName ?? "?")",
                                  systemImage: "lock.fill")
                            Button {
                                Task { await releaseLock() }
                            } label: { Label("Sperre lösen", systemImage: "lock.open") }
                        } else {
                            Button {
                                Task { await acquireLock() }
                            } label: { Label("Sperren (30 Min)", systemImage: "lock") }
                        }
                        Divider()
                        Button(role: .destructive) {
                            showReport = true
                        } label: { Label("Datei melden…", systemImage: "flag") }
                    } label: {
                        Image(systemName: lockStatus?.locked == true ? "lock.circle.fill" : "ellipsis.circle")
                            .foregroundStyle(lockStatus?.locked == true ? Color.orange : Color.accentColor)
                    }
                }
            }
        }
        .task {
            await download()
            await loadLock()
        }
        .sheet(isPresented: $showReport) {
            ReportSheet(subjectKind: .file, subjectId: file.id,
                        subjectLabel: file.name,
                        subjectOwnerUserId: nil, subjectOwnerName: nil)
        }
    }

    // v1.10.88: File-Lock-Helpers
    private func loadLock() async {
        guard let api = auth.api else { return }
        do { lockStatus = try await api.fileLockStatus(file.id) }
        catch { /* 404 auf alten Server → egal */ }
    }
    private func acquireLock() async {
        guard let api = auth.api else { return }
        do { try await api.fileLockAcquire(file.id); await loadLock() }
        catch let ex { error = ex.localizedDescription }
    }
    private func releaseLock() async {
        guard let api = auth.api else { return }
        do { try await api.fileLockRelease(file.id); await loadLock() }
        catch let ex { error = ex.localizedDescription }
    }

    private func download() async {
        guard let api = auth.api else { return }
        busy = true; error = nil
        defer { busy = false }
        do {
            let resp = try await api.previewUrl(fileId: file.id)
            guard let url = URL(string: resp.url) else { throw ApiError.network("Bad SAS URL") }
            let (tmp, _) = try await URLSession.shared.download(from: url)
            // v1.10.79: TmpFile-Helper — UUID-Unterordner verhindert
            // Kollision bei gleichnamigen Files aus verschiedenen Ordnern.
            let dest = TmpFile.destinationURL(for: file.name)
            try FileManager.default.moveItem(at: tmp, to: dest)
            localURL = dest
        } catch let e as ApiError { error = e.localizedDescription }
        catch let ex { error = ex.localizedDescription }
    }
}

struct QLQuickLookView: UIViewControllerRepresentable {
    let url: URL

    func makeCoordinator() -> Coordinator { Coordinator(url: url) }

    func makeUIViewController(context: Context) -> QLPreviewController {
        let vc = QLPreviewController()
        vc.dataSource = context.coordinator
        return vc
    }

    // v1.10.151: Update-Pfad — vorher leer, damit blieb ein re-nutzter
    // QLPreviewController auf der ursprünglichen URL hängen, auch wenn
    // SwiftUI mit einer neuen URL updated. Jetzt: Coordinator-URL nachziehen
    // und reloadData() rufen.
    func updateUIViewController(_ uiViewController: QLPreviewController, context: Context) {
        if context.coordinator.url != url {
            context.coordinator.url = url
            uiViewController.reloadData()
        }
    }

    final class Coordinator: NSObject, QLPreviewControllerDataSource {
        var url: URL
        init(url: URL) { self.url = url }
        func numberOfPreviewItems(in controller: QLPreviewController) -> Int { 1 }
        func previewController(_ controller: QLPreviewController, previewItemAt index: Int) -> QLPreviewItem {
            url as QLPreviewItem
        }
    }
}
