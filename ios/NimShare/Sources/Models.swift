import Foundation

// MARK: - Auth
struct LoginResponse: Codable {
    let token: String
    let expiresAt: Date
    let user: UserDto
}

struct UserDto: Codable, Identifiable, Equatable {
    let id: UUID
    let email: String
    let displayName: String
    let role: String
    let avatarUrl: String?
    let quotaBytes: Int64
    let preferredCulture: String
}

// MARK: - Browse
struct ScopeTile: Codable, Identifiable, Hashable {
    let scope: String
    let groupId: UUID?
    let name: String

    var id: String {
        scope + (groupId?.uuidString ?? "")
    }
    var systemImage: String {
        switch scope.lowercased() {
        case "personal": return "person.crop.circle.fill"
        case "public": return "globe.europe.africa.fill"
        case "group": return "person.3.fill"
        default: return "folder.fill"
        }
    }
}

struct FolderItem: Codable, Identifiable, Hashable {
    let id: UUID
    let name: String
}

struct FileItem: Codable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let sizeBytes: Int64
    let contentType: String
    let createdAt: Date
    let ownerName: String?
    let aiTags: String?
    let aiRiskFlag: String?

    var tags: [String] {
        guard let t = aiTags, !t.isEmpty else { return [] }
        return t.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
    }
    var iconName: String {
        let ct = contentType.lowercased()
        if ct.hasPrefix("image/") { return "photo" }
        if ct.hasPrefix("video/") { return "film" }
        if ct.hasPrefix("audio/") { return "waveform" }
        if ct.contains("pdf") { return "doc.richtext" }
        if ct.contains("zip") || ct.contains("compressed") { return "archivebox" }
        if ct.contains("word") || ct.contains("document") { return "doc.text" }
        if ct.contains("sheet") || ct.contains("excel") { return "tablecells" }
        if ct.contains("presentation") || ct.contains("powerpoint") { return "chart.bar.doc.horizontal" }
        if ct.hasPrefix("text/") { return "doc.plaintext" }
        return "doc"
    }
}

struct Breadcrumb: Codable, Hashable {
    let name: String
    let path: String
}

struct BrowseResponse: Codable {
    let subfolders: [FolderItem]
    let files: [FileItem]
    let breadcrumbs: [Breadcrumb]
    let currentFolderId: UUID
    let canWrite: Bool
    let canManage: Bool
}

// MARK: - Links (mirror of server LinkDto)
struct ShareLinkDto: Codable, Identifiable, Hashable {
    let id: UUID
    let slug: String
    let url: String
    let qrCodeUrl: String
    let expiresAt: Date?
    let maxDownloads: Int?
    let downloadCount: Int
    let hitCount: Int
    let hasPassword: Bool
    let isRevoked: Bool
    let createdAt: Date
}

// MARK: - AI Search & Chat (mirror of server SearchHit)
struct SearchHitDto: Codable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let score: Double
    let snippet: String?
    let folderId: UUID?
}

struct ChatResponseDto: Codable {
    let answer: String
    let citations: [SearchHitDto]
}

// MARK: - Preview URL
struct PreviewUrlResponse: Codable {
    let url: String
    let contentType: String?
}

// MARK: - API Error
enum ApiError: LocalizedError {
    case notAuthorized
    case notFound
    case http(Int, String?)
    case decoding(String)
    case network(String)

    var errorDescription: String? {
        switch self {
        case .notAuthorized: return NSLocalizedString("Not signed in.", comment: "")
        case .notFound: return NSLocalizedString("Not found.", comment: "")
        case .http(let code, let body): return "HTTP \(code)\(body.map { ": \($0)" } ?? "")"
        case .decoding(let msg): return "Decoding failed: \(msg)"
        case .network(let msg): return msg
        }
    }
}
