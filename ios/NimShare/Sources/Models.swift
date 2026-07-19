import Foundation

// MARK: - Auth
struct LoginResponse: Codable {
    let token: String
    let expiresAt: Date
    let user: UserDto
}

/// Envelope returned by /api/v1/auth/login when the user has 2FA enrolled.
struct TotpChallengeResponse: Codable {
    let requiresTotp: Bool
    let challengeToken: String
}

/// Server login response is *either* the LoginResponse or the challenge.
/// Both cases share a HTTP 200 status; the client discriminates on
/// `requiresTotp`.
enum LoginResult {
    case success(LoginResponse)
    case totpRequired(String)  // challenge token
}

// MARK: - 2FA
struct TotpStatus: Codable {
    let enabled: Bool
    let enrolledAt: Date?
}

struct TotpInitResponse: Codable {
    let secret: String
    let otpAuthUri: String
}

// MARK: - Notifications
struct NotifyDto: Codable, Identifiable, Hashable {
    let id: UUID
    let kind: String
    let title: String
    let body: String?
    let href: String?
    let fileId: UUID?
    let createdAt: Date
    let readAt: Date?

    var isUnread: Bool { readAt == nil }

    var iconName: String {
        switch kind {
        case "DirectShareGranted": return "person.crop.circle.badge.plus"
        case "LinkDownloaded": return "arrow.down.circle.fill"
        case "InviteAccepted": return "checkmark.seal.fill"
        case "QuotaWarning": return "exclamationmark.triangle.fill"
        case "SystemAnnouncement": return "megaphone.fill"
        default: return "bell.fill"
        }
    }
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

// MARK: - Trash
struct TrashItemDto: Codable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let sizeBytes: Int64
    let contentType: String
    let deletedAt: Date?
    let ownerName: String?
}

// MARK: - Favorites
struct FavoriteDto: Codable, Identifiable, Hashable {
    let id: UUID
    let kind: String     // "file" | "folder"
    let targetId: UUID
    let name: String
    let createdAt: Date
}

struct ToggleFavoriteResponse: Codable {
    let starred: Bool
}

// MARK: - Direct shares (Berechtigungen)
enum DirectSharePermission: String, Codable, CaseIterable, Identifiable {
    case read = "Read"
    case write = "Write"
    var id: String { rawValue }
    var localized: String {
        switch self {
        case .read: return NSLocalizedString("Read", comment: "")
        case .write: return NSLocalizedString("Write", comment: "")
        }
    }
}

struct DirectShareDto: Codable, Identifiable, Hashable {
    let id: UUID
    let fileId: UUID?
    let folderId: UUID?
    let userId: UUID?
    let userDisplayName: String?
    let groupId: UUID?
    let groupName: String?
    let permission: String
    let createdAt: Date

    var permissionEnum: DirectSharePermission { DirectSharePermission(rawValue: permission) ?? .read }
    var displayName: String { userDisplayName ?? groupName ?? "?" }
    var isGroup: Bool { groupId != nil }
}

struct DirectShareUserOption: Codable, Identifiable, Hashable {
    let id: UUID
    let displayName: String
    let email: String
}

struct DirectShareGroupOption: Codable, Identifiable, Hashable {
    let id: UUID
    let name: String
}

struct SharedWithMeItemDto: Codable, Identifiable, Hashable {
    let kind: String     // "file" | "folder"
    let id: UUID
    let name: String
    let permission: String
    let sharedByName: String
    let sharedAt: Date

    var permissionEnum: DirectSharePermission { DirectSharePermission(rawValue: permission) ?? .read }
}

// MARK: - Activity
struct ActivityDto: Codable, Identifiable, Hashable {
    let kind: String
    let actorName: String
    let summary: String
    let fileId: UUID?
    let folderId: UUID?
    let groupId: UUID?
    let targetUserId: UUID?
    let at: Date

    // Stable per-instance id — the server doesn't emit one for activity, so
    // we synthesise a UUID at decode time. Using timestamp+kind collides on
    // bulk uploads and makes SwiftUI ForEach diffing flicker.
    let localId: UUID = UUID()
    var id: UUID { localId }

    private enum CodingKeys: String, CodingKey {
        case kind, actorName, summary, fileId, folderId, groupId, targetUserId, at
    }

    var iconName: String {
        switch kind {
        case "FileUploaded": return "arrow.up.doc.fill"
        case "FileDeleted": return "trash.fill"
        case "FileRenamed", "FolderRenamed": return "pencil"
        case "FileMoved": return "arrowshape.turn.up.right.fill"
        case "FolderCreated": return "folder.badge.plus"
        case "FolderDeleted": return "folder.badge.minus"
        case "ShareLinkCreated": return "link.badge.plus"
        case "ShareLinkRevoked": return "link.slash"
        case "DirectShareGranted": return "person.crop.circle.badge.plus"
        case "DirectShareRevoked": return "person.crop.circle.badge.xmark"
        case "GroupCreated", "GroupMemberAdded", "GroupMemberRemoved": return "person.3.fill"
        case "UserSignedIn": return "person.crop.circle.fill.badge.checkmark"
        case "UserInvited": return "envelope.badge.fill"
        default: return "clock.fill"
        }
    }
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
