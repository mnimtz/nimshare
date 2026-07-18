import SwiftUI
import QuickLook

struct FilePreviewView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let file: FileItem

    @State private var localURL: URL?
    @State private var busy = true
    @State private var error: String?

    var body: some View {
        Group {
            if busy {
                ProgressView("Loading…").frame(maxWidth: .infinity, maxHeight: .infinity)
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
                Button("Close") { dismiss() }
            }
            ToolbarItem(placement: .topBarTrailing) {
                if let url = localURL {
                    ShareLink(item: url) { Image(systemName: "square.and.arrow.up") }
                }
            }
        }
        .task { await download() }
    }

    private func download() async {
        guard let api = auth.api else { return }
        busy = true; error = nil
        defer { busy = false }
        do {
            let resp = try await api.previewUrl(fileId: file.id)
            guard let url = URL(string: resp.url) else { throw ApiError.network("Bad SAS URL") }
            let (tmp, _) = try await URLSession.shared.download(from: url)
            // Rename with original filename so QuickLook picks the right renderer.
            let dest = FileManager.default.temporaryDirectory.appendingPathComponent(file.name)
            try? FileManager.default.removeItem(at: dest)
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

    func updateUIViewController(_ uiViewController: QLPreviewController, context: Context) {}

    final class Coordinator: NSObject, QLPreviewControllerDataSource {
        let url: URL
        init(url: URL) { self.url = url }
        func numberOfPreviewItems(in controller: QLPreviewController) -> Int { 1 }
        func previewController(_ controller: QLPreviewController, previewItemAt index: Int) -> QLPreviewItem {
            url as QLPreviewItem
        }
    }
}
