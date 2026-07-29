import SwiftUI
import UIKit

// v1.10.195: Ansichts-Umschalter für den Datei-Browser — Liste, Kacheln
// (3 Spalten, kleines Thumbnail/Icon + Name) und Vorschau (2 Spalten,
// großes Thumbnail + Name + Größe). Gemeinsame Bausteine, genutzt von
// FolderBrowserView und SharedFolderView. Persistiert via @AppStorage
// unter "browser.viewMode".

enum BrowserViewMode: String, CaseIterable, Identifiable {
    case list, grid, preview
    var id: String { rawValue }

    var icon: String {
        switch self {
        case .list: return "list.bullet"
        case .grid: return "square.grid.3x3"
        case .preview: return "square.grid.2x2"
        }
    }
}

/// Toolbar-Menü zum Umschalten des Ansichtsmodus. Als eigene View, damit
/// FolderBrowserView und SharedFolderView exakt dasselbe Menü zeigen.
struct BrowserViewModeMenu: View {
    @Binding var rawMode: String

    var body: some View {
        Menu {
            Picker("Ansicht", selection: $rawMode) {
                Label("Liste", systemImage: BrowserViewMode.list.icon)
                    .tag(BrowserViewMode.list.rawValue)
                Label("Kacheln", systemImage: BrowserViewMode.grid.icon)
                    .tag(BrowserViewMode.grid.rawValue)
                Label("Vorschau", systemImage: BrowserViewMode.preview.icon)
                    .tag(BrowserViewMode.preview.rawValue)
            }
        } label: {
            Image(systemName: "square.grid.2x2")
        }
    }
}

// MARK: - Thumbnail-Loader

/// Lädt Thumbnails über das Bearer-geschützte /api/v1/files/{id}/thumb-
/// Endpoint. Das antwortet mit 302 auf eine Azure-SAS-URL — AsyncImage kann
/// keine Auth-Header, deshalb hier eine eigene URLSession, deren Delegate
/// beim Redirect den Authorization-Header strippt (Azure Blob lehnt
/// Requests mit SAS in der URL UND fremdem Bearer-Header ab).
/// 404 (Thumb nicht generiert / kein Bild) wird als Negativ-Ergebnis
/// gecacht, damit beim Scrollen nicht erneut angefragt wird.
actor ThumbLoader {
    static let shared = ThumbLoader()

    private let cache = NSCache<NSUUID, UIImage>()
    private var failed: Set<UUID> = []
    private var inflight: [UUID: Task<FetchResult, Never>] = [:]
    private let session: URLSession

    init() {
        session = URLSession(configuration: .default,
                             delegate: AuthStrippingRedirectDelegate(),
                             delegateQueue: nil)
        cache.countLimit = 400
    }

    private enum FetchResult {
        case image(UIImage)
        /// Server said 404 (or a 2xx body that isn't a decodable image) — this
        /// fileId genuinely has no thumb, safe to negative-cache.
        case permanentMiss
        /// Timeout, dropped connection, 5xx, etc. — say nothing about whether
        /// a thumb exists; must retry on next scroll-into-view instead of
        /// being cached as a permanent failure forever.
        case transientFailure
    }

    func image(for fileId: UUID, request: URLRequest) async -> UIImage? {
        if let hit = cache.object(forKey: fileId as NSUUID) { return hit }
        if failed.contains(fileId) { return nil }
        if let running = inflight[fileId] {
            if case .image(let img) = await running.value { return img }
            return nil
        }
        let task = Task<FetchResult, Never> { [session] in
            guard let (data, resp) = try? await session.data(for: request),
                  let http = resp as? HTTPURLResponse else { return .transientFailure }
            if http.statusCode == 200, let img = UIImage(data: data) { return .image(img) }
            if http.statusCode == 404 { return .permanentMiss }
            return .transientFailure
        }
        inflight[fileId] = task
        let result = await task.value
        inflight[fileId] = nil
        switch result {
        case .image(let img):
            cache.setObject(img, forKey: fileId as NSUUID)
            return img
        case .permanentMiss:
            if !Task.isCancelled { failed.insert(fileId) }
            return nil
        case .transientFailure:
            return nil
        }
    }
}

/// Strippt beim 302-Redirect (API → Azure-SAS-URL) den Authorization-
/// Header. URLSession kopiert die Original-Header sonst auf den Redirect-
/// Request weiter — Azure Blob antwortet dann mit 403.
final class AuthStrippingRedirectDelegate: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        var stripped = request
        stripped.setValue(nil, forHTTPHeaderField: "Authorization")
        completionHandler(stripped)
    }
}

// MARK: - Kachel-Views

/// Ordner-Kachel für grid/preview-Modus.
struct FolderTileView: View {
    let name: String
    let mode: BrowserViewMode

    var body: some View {
        VStack(spacing: 6) {
            RoundedRectangle(cornerRadius: 10)
                .fill(Theme.cardBackground)
                .aspectRatio(1, contentMode: .fit)
                .overlay {
                    Image(systemName: "folder.fill")
                        .font(.system(size: mode == .preview ? 52 : 34))
                        .foregroundStyle(Theme.tungstenBlue)
                }
            Text(name)
                .font(mode == .preview ? .footnote : .caption2)
                .lineLimit(2)
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity)
            if mode == .preview {
                // Platzhalter-Zeile, damit Ordner- und Datei-Kacheln im
                // Vorschau-Modus gleich hoch layouten.
                Text(" ").font(.caption2)
            }
        }
        .contentShape(Rectangle())
    }
}

/// Datei-Kachel für grid/preview-Modus: Thumbnail (falls vorhanden),
/// sonst Format-Badge. Preview-Modus zeigt zusätzlich die Dateigröße.
struct FileTileView: View {
    let file: FileItem
    let mode: BrowserViewMode

    var body: some View {
        VStack(spacing: 6) {
            FileThumbView(file: file, iconSize: mode == .preview ? 52 : 34)
            Text(file.name)
                .font(mode == .preview ? .footnote : .caption2)
                .lineLimit(2)
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity)
            if mode == .preview {
                Text(ByteCountFormatter.string(fromByteCount: file.sizeBytes, countStyle: .file))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .contentShape(Rectangle())
    }
}

/// Quadratisches Thumbnail mit Datei-Icon-Fallback (404 / kein Bild).
struct FileThumbView: View {
    @EnvironmentObject var auth: AuthStore
    let file: FileItem
    let iconSize: CGFloat

    @State private var image: UIImage?

    var body: some View {
        RoundedRectangle(cornerRadius: 10)
            .fill(Theme.cardBackground)
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                } else {
                    FileFormatBadge(name: file.name, size: iconSize)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .task(id: file.id) {
                guard image == nil, let api = auth.api else { return }
                let req = api.thumbnailRequest(fileId: file.id, size: 400)
                if let img = await ThumbLoader.shared.image(for: file.id, request: req) {
                    image = img
                }
            }
    }
}
