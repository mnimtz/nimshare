import Foundation

@MainActor
final class NimShareAPI: ObservableObject {
    let baseURL: URL
    private var token: String?

    init(baseURL: URL, token: String? = nil) {
        self.baseURL = baseURL
        self.token = token
    }

    func setToken(_ token: String?) { self.token = token }

    // MARK: - Encoding

    private static let jsonEncoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    private static let jsonDecoder: JSONDecoder = {
        let d = JSONDecoder()
        // Server emits ISO8601 with fractional seconds *and* offset. Custom strategy
        // that tries both, otherwise Date fields fail silently. Formatters are
        // built inside the closure so no non-Sendable captures leak in.
        d.dateDecodingStrategy = .custom { decoder in
            let iso = ISO8601DateFormatter()
            iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            let isoNoFrac = ISO8601DateFormatter()
            isoNoFrac.formatOptions = [.withInternetDateTime]
            let c = try decoder.singleValueContainer()
            let s = try c.decode(String.self)
            if let parsed = iso.date(from: s) { return parsed }
            if let parsed = isoNoFrac.date(from: s) { return parsed }
            throw DecodingError.dataCorruptedError(in: c, debugDescription: "Bad date: \(s)")
        }
        return d
    }()

    // MARK: - Request builder

    private func request(_ method: String, _ path: String, query: [URLQueryItem] = [], body: Data? = nil, contentType: String? = nil) -> URLRequest {
        var comp = URLComponents(url: baseURL.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        if !query.isEmpty { comp.queryItems = query }
        var req = URLRequest(url: comp.url!)
        req.httpMethod = method
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        if let ct = contentType { req.setValue(ct, forHTTPHeaderField: "Content-Type") }
        if let t = token { req.setValue("Bearer \(t)", forHTTPHeaderField: "Authorization") }
        if let b = body { req.httpBody = b }
        return req
    }

    private func perform(_ req: URLRequest) async throws -> (Data, HTTPURLResponse) {
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            guard let http = resp as? HTTPURLResponse else {
                throw ApiError.network("No HTTP response")
            }
            if http.statusCode == 401 { throw ApiError.notAuthorized }
            if http.statusCode == 404 { throw ApiError.notFound }
            if !(200..<300).contains(http.statusCode) {
                throw ApiError.http(http.statusCode, String(data: data, encoding: .utf8))
            }
            return (data, http)
        } catch let e as ApiError { throw e }
        catch { throw ApiError.network(error.localizedDescription) }
    }

    private func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        do { return try Self.jsonDecoder.decode(T.self, from: data) }
        catch { throw ApiError.decoding(String(describing: error)) }
    }

    // MARK: - Endpoints

    struct LoginBody: Encodable { let email: String; let password: String }
    /// Two-shot response — either a full LoginResponse or a TOTP challenge.
    func login(email: String, password: String) async throws -> LoginResult {
        let body = try Self.jsonEncoder.encode(LoginBody(email: email, password: password))
        let req = request("POST", "api/v1/auth/login", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        // Try challenge first — the JSON discriminator is cheap.
        if let ch = try? Self.jsonDecoder.decode(TotpChallengeResponse.self, from: data), ch.requiresTotp {
            return .totpRequired(ch.challengeToken)
        }
        return .success(try decode(LoginResponse.self, data))
    }

    struct TotpSubmitBody: Encodable { let challengeToken: String; let code: String }
    func loginTotp(challengeToken: String, code: String) async throws -> LoginResponse {
        let body = try Self.jsonEncoder.encode(TotpSubmitBody(challengeToken: challengeToken, code: code))
        let req = request("POST", "api/v1/auth/login/totp", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(LoginResponse.self, data)
    }

    // MARK: - 2FA
    func totpStatus() async throws -> TotpStatus {
        let req = request("GET", "api/v1/2fa/status")
        let (data, _) = try await perform(req)
        return try decode(TotpStatus.self, data)
    }

    func totpInit() async throws -> TotpInitResponse {
        let req = request("POST", "api/v1/2fa/setup/init")
        let (data, _) = try await perform(req)
        return try decode(TotpInitResponse.self, data)
    }

    struct TotpVerifyBody: Encodable { let secret: String; let code: String }
    func totpVerify(secret: String, code: String) async throws {
        let body = try Self.jsonEncoder.encode(TotpVerifyBody(secret: secret, code: code))
        let req = request("POST", "api/v1/2fa/setup/verify", body: body, contentType: "application/json")
        _ = try await perform(req)
    }

    struct TotpDisableBody: Encodable { let code: String }
    func totpDisable(code: String) async throws {
        let body = try Self.jsonEncoder.encode(TotpDisableBody(code: code))
        let req = request("POST", "api/v1/2fa/disable", body: body, contentType: "application/json")
        _ = try await perform(req)
    }

    // MARK: - Notifications list
    func listNotifications(onlyUnread: Bool = false, limit: Int = 100) async throws -> [NotifyDto] {
        let req = request("GET", "api/v1/notifications", query: [
            .init(name: "onlyUnread", value: onlyUnread ? "true" : "false"),
            .init(name: "limit", value: String(limit)),
        ])
        let (data, _) = try await perform(req)
        return try decode([NotifyDto].self, data)
    }

    func markNotificationRead(_ id: UUID) async throws {
        let req = request("POST", "api/v1/notifications/\(id)/read")
        _ = try await perform(req)
    }

    func markAllNotificationsRead() async throws {
        let req = request("POST", "api/v1/notifications/read-all")
        _ = try await perform(req)
    }

    // MARK: - Signatures
    func listMySignatureRequests() async throws -> [SignatureRequestDto] {
        let req = request("GET", "api/v1/signatures")
        let (data, _) = try await perform(req)
        return try decode([SignatureRequestDto].self, data)
    }

    func signatureRequestDetail(_ id: UUID) async throws -> SignatureRequestDto {
        let req = request("GET", "api/v1/signatures/\(id)")
        let (data, _) = try await perform(req)
        return try decode(SignatureRequestDto.self, data)
    }

    struct CreateSignatureBody: Encodable {
        let sourceFileId: UUID
        let title: String?
        let message: String?
        let deliveryOrder: String
        let deadline: Date?
    }
    func createSignatureRequest(sourceFileId: UUID, title: String? = nil, message: String? = nil,
                                deliveryOrder: String = "Parallel",
                                deadline: Date? = nil) async throws -> SignatureRequestDto {
        let body = try Self.jsonEncoder.encode(CreateSignatureBody(
            sourceFileId: sourceFileId, title: title, message: message,
            deliveryOrder: deliveryOrder, deadline: deadline))
        let req = request("POST", "api/v1/signatures", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(SignatureRequestDto.self, data)
    }

    struct AddParticipantBody: Encodable {
        let email: String; let name: String; let role: String; let order: Int
    }
    func addSignatureParticipant(_ requestId: UUID, email: String, name: String,
                                 role: String = "Signer", order: Int = 0) async throws -> UUID {
        let body = try Self.jsonEncoder.encode(AddParticipantBody(email: email, name: name, role: role, order: order))
        let req = request("POST", "api/v1/signatures/\(requestId)/participants", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        struct R: Decodable { let id: UUID }
        return try decode(R.self, data).id
    }

    struct AddFieldBody: Encodable {
        let participantId: UUID; let type: String; let page: Int; let anchor: String; let label: String?
    }
    func addSignatureField(_ requestId: UUID, participantId: UUID, type: String = "Signature",
                           page: Int = 1, anchor: String = "BottomCenter", label: String? = nil) async throws -> UUID {
        let body = try Self.jsonEncoder.encode(AddFieldBody(participantId: participantId, type: type, page: page, anchor: anchor, label: label))
        let req = request("POST", "api/v1/signatures/\(requestId)/fields", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        struct R: Decodable { let id: UUID }
        return try decode(R.self, data).id
    }

    func sendSignatureRequest(_ id: UUID) async throws -> SignatureRequestDto {
        let req = request("POST", "api/v1/signatures/\(id)/send")
        let (data, _) = try await perform(req)
        return try decode(SignatureRequestDto.self, data)
    }

    func cancelSignatureRequest(_ id: UUID) async throws {
        let req = request("POST", "api/v1/signatures/\(id)/cancel")
        _ = try await perform(req)
    }

    // v1.10.56 iOS: neue Endpoints aus Web-v1.10.40+ nachgezogen.
    // Force-Finalize für Vorgänge die auf "Sent" hängen. Antwort:
    // entweder pending-Liste (wer fehlt), oder success. Server-side
    // ist die Response-Struktur ein loses Dict, wir mappen die
    // wichtigsten Felder als optional in einem Response-Struct.
    struct FinalizeResponse: Decodable {
        let status: String?
        let finalFileId: UUID?
        let note: String?
        let pending: [PendingParticipant]?
        let detail: String?
        struct PendingParticipant: Decodable {
            let id: UUID?
            let name: String?
            let email: String?
            let role: String?
            let status: String?
        }
    }
    func forceFinalizeSignature(_ id: UUID) async throws -> FinalizeResponse {
        let req = request("POST", "api/v1/signatures/\(id)/finalize")
        let (data, _) = try await perform(req)
        return try decode(FinalizeResponse.self, data)
    }

    // Signed-PDF-Download — direkter Zugriff auf das finalisierte PDF
    // via API (statt Umweg über /browse/personal). Gibt Data + suggested
    // Filename zurück. Der iOS-Aufrufer schickt das an QuickLook oder
    // in einen Share-Sheet.
    func downloadSignedPdf(_ id: UUID) async throws -> (Data, String) {
        let req = request("GET", "api/v1/signatures/\(id)/signed-pdf")
        let (data, resp) = try await perform(req)
        // Content-Disposition parsen für den Dateinamen, sonst
        // Fallback auf "signed-<id>.pdf"
        var filename = "signed-\(id.uuidString).pdf"
        if let cd = resp.value(forHTTPHeaderField: "Content-Disposition") {
            if let range = cd.range(of: "filename=\"") {
                let rest = cd[range.upperBound...]
                if let end = rest.firstIndex(of: "\"") {
                    filename = String(rest[..<end])
                }
            }
        }
        return (data, filename)
    }

    // Delete für Signatur-Vorgänge (Web-UI hat sig.confirm_delete).
    func deleteSignatureRequest(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/signatures/\(id)")
        _ = try await perform(req)
    }

    func unreadNotificationCount() async throws -> Int {
        let req = request("GET", "api/v1/notifications/unread-count")
        let (data, _) = try await perform(req)
        struct R: Decodable { let unread: Int }
        return try decode(R.self, data).unread
    }

    func me() async throws -> UserDto {
        let req = request("GET", "api/v1/auth/me")
        let (data, _) = try await perform(req)
        return try decode(UserDto.self, data)
    }

    func scopes() async throws -> [ScopeTile] {
        let req = request("GET", "api/v1/browse/scopes")
        let (data, _) = try await perform(req)
        return try decode([ScopeTile].self, data)
    }

    func browse(scope: String, groupId: UUID?, path: String?) async throws -> BrowseResponse {
        var q: [URLQueryItem] = [.init(name: "scope", value: scope)]
        if let g = groupId { q.append(.init(name: "groupId", value: g.uuidString)) }
        if let p = path { q.append(.init(name: "path", value: p)) }
        let req = request("GET", "api/v1/browse/list", query: q)
        let (data, _) = try await perform(req)
        return try decode(BrowseResponse.self, data)
    }

    func previewUrl(fileId: UUID) async throws -> PreviewUrlResponse {
        let req = request("GET", "api/v1/files/\(fileId)/preview-url")
        let (data, _) = try await perform(req)
        return try decode(PreviewUrlResponse.self, data)
    }

    func listMyLinks() async throws -> [ShareLinkDto] {
        let req = request("GET", "api/v1/links")
        let (data, _) = try await perform(req)
        // Server returns a bare array; keep a wrapper fallback just in case
        // future versions add pagination.
        if let arr = try? Self.jsonDecoder.decode([ShareLinkDto].self, from: data) { return arr }
        struct Wrapper: Decodable { let items: [ShareLinkDto] }
        return try Self.jsonDecoder.decode(Wrapper.self, from: data).items
    }

    struct SearchBody: Encodable {
        let query: String
        let scope: String
        let groupId: UUID?
        let limit: Int
    }
    func semanticSearch(query: String, scope: String = "", groupId: UUID? = nil, limit: Int = 20) async throws -> [SearchHitDto] {
        let body = try Self.jsonEncoder.encode(SearchBody(query: query, scope: scope, groupId: groupId, limit: limit))
        let req = request("POST", "api/v1/ai/search", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode([SearchHitDto].self, data)
    }

    struct ChatBody: Encodable {
        let question: String
        let scope: String
        let groupId: UUID?
    }
    func chatAsk(question: String, scope: String = "", groupId: UUID? = nil) async throws -> ChatResponseDto {
        let body = try Self.jsonEncoder.encode(ChatBody(question: question, scope: scope, groupId: groupId))
        let req = request("POST", "api/v1/ai/chat", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(ChatResponseDto.self, data)
    }

    // MARK: - Trash

    func listTrash() async throws -> [TrashItemDto] {
        let req = request("GET", "api/v1/trash")
        let (data, _) = try await perform(req)
        return try decode([TrashItemDto].self, data)
    }

    func restoreFromTrash(_ id: UUID) async throws {
        let req = request("POST", "api/v1/trash/\(id)/restore")
        _ = try await perform(req)
    }

    func purgeFromTrash(_ id: UUID) async throws {
        let req = request("POST", "api/v1/trash/\(id)/purge")
        _ = try await perform(req)
    }

    func deleteFile(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/files/\(id)")
        _ = try await perform(req)
    }

    // MARK: - Favorites

    func listFavorites() async throws -> [FavoriteDto] {
        let req = request("GET", "api/v1/favorites")
        let (data, _) = try await perform(req)
        return try decode([FavoriteDto].self, data)
    }

    struct ToggleFavoriteBody: Encodable {
        let fileId: UUID?
        let folderId: UUID?
    }
    func toggleFavorite(fileId: UUID? = nil, folderId: UUID? = nil) async throws -> Bool {
        let body = try Self.jsonEncoder.encode(ToggleFavoriteBody(fileId: fileId, folderId: folderId))
        let req = request("POST", "api/v1/favorites/toggle", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(ToggleFavoriteResponse.self, data).starred
    }

    // MARK: - Direct shares

    func directShares(forFile id: UUID) async throws -> [DirectShareDto] {
        let req = request("GET", "api/v1/direct-shares/for-file/\(id)")
        let (data, _) = try await perform(req)
        return try decode([DirectShareDto].self, data)
    }

    func directShares(forFolder id: UUID) async throws -> [DirectShareDto] {
        let req = request("GET", "api/v1/direct-shares/for-folder/\(id)")
        let (data, _) = try await perform(req)
        return try decode([DirectShareDto].self, data)
    }

    func searchShareableUsers(_ q: String) async throws -> [DirectShareUserOption] {
        let req = request("GET", "api/v1/direct-shares/users", query: [.init(name: "q", value: q)])
        let (data, _) = try await perform(req)
        return try decode([DirectShareUserOption].self, data)
    }

    func listShareableGroups() async throws -> [DirectShareGroupOption] {
        let req = request("GET", "api/v1/direct-shares/groups")
        let (data, _) = try await perform(req)
        return try decode([DirectShareGroupOption].self, data)
    }

    struct CreateDirectShareBody: Encodable {
        let fileId: UUID?
        let folderId: UUID?
        let userId: UUID?
        let groupId: UUID?
        let permission: String
    }
    func createDirectShare(fileId: UUID? = nil, folderId: UUID? = nil,
                           userId: UUID? = nil, groupId: UUID? = nil,
                           permission: DirectSharePermission) async throws {
        let body = try Self.jsonEncoder.encode(CreateDirectShareBody(
            fileId: fileId, folderId: folderId, userId: userId, groupId: groupId,
            permission: permission.rawValue))
        let req = request("POST", "api/v1/direct-shares", body: body, contentType: "application/json")
        _ = try await perform(req)
    }

    func revokeDirectShare(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/direct-shares/\(id)")
        _ = try await perform(req)
    }

    // MARK: - v1.10.104: Folder permissions (Public „Windows-ACL")

    func folderPermissions(id: UUID) async throws -> FolderPermissionsDto {
        let req = request("GET", "api/v1/folders/\(id)/permissions")
        let (data, _) = try await perform(req)
        return try decode(FolderPermissionsDto.self, data)
    }

    struct SetFolderPrivacyBody: Encodable { let isPrivate: Bool }
    struct SetFolderPrivacyResponse: Decodable { let id: UUID; let isPrivate: Bool }
    func setFolderPrivacy(id: UUID, isPrivate: Bool) async throws -> Bool {
        let body = try Self.jsonEncoder.encode(SetFolderPrivacyBody(isPrivate: isPrivate))
        let req = request("PATCH", "api/v1/folders/\(id)/privacy", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(SetFolderPrivacyResponse.self, data).isPrivate
    }

    func sharedWithMe() async throws -> [SharedWithMeItemDto] {
        let req = request("GET", "api/v1/direct-shares/shared-with-me")
        let (data, _) = try await perform(req)
        return try decode([SharedWithMeItemDto].self, data)
    }

    // MARK: - Share-Link / Upload-Request (v1.10.66 iOS parity)

    struct CreateShareLinkBody: Encodable {
        let fileId: UUID?
        let folderId: UUID?
        let slug: String?
        let password: String?
        let maxDownloads: Int?
        let expiresAt: Date?
        let message: String?
        let notifyOnAccess: Bool
    }
    /// Create a share link with default options (no password, no expiry, no
    /// download limit). Returns the freshly created ShareLinkDto — caller
    /// pastes/shows the .url.
    func createShareLink(fileId: UUID? = nil, folderId: UUID? = nil) async throws -> ShareLinkDto {
        let body = try Self.jsonEncoder.encode(CreateShareLinkBody(
            fileId: fileId, folderId: folderId, slug: nil, password: nil,
            maxDownloads: nil, expiresAt: nil, message: nil, notifyOnAccess: false))
        let req = request("POST", "api/v1/links", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(ShareLinkDto.self, data)
    }

    struct CreateUploadRequestBody: Encodable {
        let slug: String?
        let password: String?
        let maxUploads: Int?
        let expiresAt: Date?
        let message: String?
        let targetFolder: String
        let notifyOnUpload: Bool
    }
    struct UploadRequestResult: Decodable {
        let id: UUID
        let slug: String
        let url: String
    }
    /// Create an upload-request link (reverse-share). Uploaded files land in
    /// the owner's Personal → "Received" folder (server default).
    func createUploadRequest(message: String? = nil) async throws -> UploadRequestResult {
        let body = try Self.jsonEncoder.encode(CreateUploadRequestBody(
            slug: nil, password: nil, maxUploads: nil, expiresAt: nil,
            message: message, targetFolder: "Received", notifyOnUpload: true))
        let req = request("POST", "api/v1/upload-requests", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(UploadRequestResult.self, data)
    }

    // MARK: - Folder/File CRUD (v1.10.70 iOS parity mit Web-Kontextmenü)

    struct CreateFolderBody: Encodable { let parentId: UUID; let name: String }
    struct CreateFolderResult: Decodable { let id: UUID; let name: String; let parentId: UUID }
    func createFolder(parentId: UUID, name: String) async throws -> CreateFolderResult {
        let body = try Self.jsonEncoder.encode(CreateFolderBody(parentId: parentId, name: name))
        let req = request("POST", "api/v1/folders", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(CreateFolderResult.self, data)
    }

    struct RenameBody: Encodable { let name: String }
    func renameFolder(id: UUID, newName: String) async throws {
        let body = try Self.jsonEncoder.encode(RenameBody(name: newName))
        let req = request("POST", "api/v1/folders/\(id)/rename", body: body, contentType: "application/json")
        _ = try await perform(req)
    }
    func renameFile(id: UUID, newName: String) async throws {
        let body = try Self.jsonEncoder.encode(RenameBody(name: newName))
        let req = request("POST", "api/v1/files/\(id)/rename", body: body, contentType: "application/json")
        _ = try await perform(req)
    }

    struct MoveFileBody: Encodable { let folderId: UUID }
    func moveFile(id: UUID, targetFolderId: UUID) async throws {
        let body = try Self.jsonEncoder.encode(MoveFileBody(folderId: targetFolderId))
        let req = request("POST", "api/v1/files/\(id)/move", body: body, contentType: "application/json")
        _ = try await perform(req)
    }
    func copyFile(id: UUID, targetFolderId: UUID) async throws {
        let body = try Self.jsonEncoder.encode(MoveFileBody(folderId: targetFolderId))
        let req = request("POST", "api/v1/files/\(id)/copy", body: body, contentType: "application/json")
        _ = try await perform(req)
    }

    /// Flat writable-all list used to populate the folder-picker tree.
    struct WritableFolderNode: Decodable, Identifiable {
        let id: UUID
        let name: String?
        let path: String?
        let scope: String
        let parentId: UUID?
        let isRoot: Bool?
    }
    func writableFoldersAll() async throws -> [WritableFolderNode] {
        let req = request("GET", "api/v1/folders/writable-all")
        let (data, _) = try await perform(req)
        return try decode([WritableFolderNode].self, data)
    }

    // MARK: - File Upload (v1.10.70 iOS parity)
    //
    // Web nutzt den 3-Schritt-Flow: POST /api/v1/files {name,size,contentType,folderId}
    // → 200 { fileId, uploadUrl } → PUT uploadUrl mit blob bytes →
    // POST /api/v1/files/{id}/complete → 200. Wir spiegeln das exakt.

    struct InitUploadBody: Encodable {
        let name: String
        let sizeBytes: Int64
        let contentType: String
        let folderId: UUID?
    }
    struct InitUploadResp: Decodable {
        let fileId: UUID
        let uploadUrl: String
        let uploadMethod: String?
    }
    /// Uploads a local file to the user's Personal library (or a given folder).
    /// Returns the created fileId once /complete succeeds.
    func uploadFile(name: String, contentType: String, folderId: UUID?, data: Data) async throws -> UUID {
        let body = try Self.jsonEncoder.encode(InitUploadBody(
            name: name, sizeBytes: Int64(data.count), contentType: contentType, folderId: folderId))
        let initReq = request("POST", "api/v1/files", body: body, contentType: "application/json")
        let (initData, _) = try await perform(initReq)
        let init_ = try decode(InitUploadResp.self, initData)

        // Direct Azure Blob PUT with the SAS URL. x-ms-blob-type BlockBlob
        // is required for a single-shot PUT of a chunked upload.
        guard let url = URL(string: init_.uploadUrl) else { throw ApiError.network("Bad upload URL") }
        var putReq = URLRequest(url: url)
        putReq.httpMethod = init_.uploadMethod ?? "PUT"
        putReq.setValue("BlockBlob", forHTTPHeaderField: "x-ms-blob-type")
        putReq.setValue(contentType, forHTTPHeaderField: "Content-Type")
        let (_, putResp) = try await URLSession.shared.upload(for: putReq, from: data)
        if let http = putResp as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw ApiError.http(http.statusCode, "Azure Blob upload failed")
        }

        // Server-side: read blob props back, persist SizeBytes, kick AI post-process.
        let completeReq = request("POST", "api/v1/files/\(init_.fileId)/complete")
        _ = try await perform(completeReq)
        return init_.fileId
    }

    // MARK: - Bulk-Actions (v1.10.72 iOS parity — Mehrfach-Selektion)

    struct BulkDeleteBody: Encodable { let ids: [UUID] }
    func bulkDeleteFiles(_ ids: [UUID]) async throws {
        let body = try Self.jsonEncoder.encode(BulkDeleteBody(ids: ids))
        let req = request("POST", "api/v1/files/bulk-delete", body: body, contentType: "application/json")
        _ = try await perform(req)
    }
    struct BulkZipBody: Encodable { let fileIds: [UUID]; let archiveName: String? }
    /// Returns the raw ZIP bytes streamed by the server. Caller writes to a
    /// temp file and hands it to a UIActivityViewController / .fileMover /
    /// „In Dateien sichern" flow.
    func bulkZipFiles(_ ids: [UUID], archiveName: String? = nil) async throws -> (Data, String) {
        let body = try Self.jsonEncoder.encode(BulkZipBody(fileIds: ids, archiveName: archiveName))
        let req = request("POST", "api/v1/files/bulk-zip", body: body, contentType: "application/json")
        let (data, http) = try await perform(req)
        let cd = http.value(forHTTPHeaderField: "Content-Disposition") ?? ""
        let m = cd.range(of: #"filename="?([^"]+)"?"#, options: .regularExpression)
            .map { String(cd[$0]).replacingOccurrences(of: #"filename="?"#, with: "", options: .regularExpression).replacingOccurrences(of: "\"", with: "") }
        return (data, m ?? "nimshare.zip")
    }

    // MARK: - Signatur-Actions (v1.10.72)

    func remindSignature(_ id: UUID) async throws {
        let req = request("POST", "api/v1/signatures/\(id)/remind")
        _ = try await perform(req)
    }
    // v1.10.79: cancelSignature entfernt — Duplikat von cancelSignatureRequest
    // (Zeile 208). Beide Methoden riefen exakt denselben Endpoint auf.

    // MARK: - File-Versions (v1.10.72 iOS parity)

    struct FileVersionDto: Codable, Identifiable, Hashable {
        let id: UUID
        let versionNumber: Int
        let sizeBytes: Int64
        let contentType: String
        let createdByName: String
        let createdAt: Date
        let isCurrent: Bool
    }
    func listFileVersions(_ fileId: UUID) async throws -> [FileVersionDto] {
        let req = request("GET", "api/v1/files/\(fileId)/versions")
        let (data, _) = try await perform(req)
        return try decode([FileVersionDto].self, data)
    }
    func restoreFileVersion(fileId: UUID, versionId: UUID) async throws {
        let req = request("POST", "api/v1/files/\(fileId)/versions/\(versionId)/restore")
        _ = try await perform(req)
    }

    // v1.10.72: Direct-Share list/remove existiert schon als
    // `directShares(forFile:)`, `directShares(forFolder:)`, `revokeDirectShare(:)`
    // — DirectShareSheet nutzt das seit v1.3.0.

    // MARK: - Contacts (v1.10.71 iOS parity)

    func listContacts(query: String? = nil) async throws -> [ContactDto] {
        var q: [URLQueryItem] = [.init(name: "limit", value: "500")]
        if let s = query, !s.isEmpty { q.append(.init(name: "q", value: s)) }
        let req = request("GET", "api/v1/contacts", query: q)
        let (data, _) = try await perform(req)
        return try decode([ContactDto].self, data)
    }
    struct CreateContactBody: Encodable {
        let email: String; let name: String; let company: String?; let notes: String?; let tags: String?
    }
    func createContact(email: String, name: String, company: String? = nil, notes: String? = nil, tags: String? = nil) async throws -> ContactDto {
        let body = try Self.jsonEncoder.encode(CreateContactBody(email: email, name: name, company: company, notes: notes, tags: tags))
        let req = request("POST", "api/v1/contacts", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(ContactDto.self, data)
    }
    func deleteContact(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/contacts/\(id)")
        _ = try await perform(req)
    }

    /// v1.10.74: NimShare-User-Directory. Alle aktiven User außer sich selbst.
    func listDirectoryUsers(query: String? = nil) async throws -> [DirectoryUserDto] {
        var q: [URLQueryItem] = [.init(name: "limit", value: "1000")]
        if let s = query, !s.isEmpty { q.append(.init(name: "q", value: s)) }
        let req = request("GET", "api/v1/contacts/directory", query: q)
        let (data, _) = try await perform(req)
        return try decode([DirectoryUserDto].self, data)
    }

    // MARK: - Certificates (v1.10.71 iOS parity)

    func listCertificates() async throws -> [CertDto] {
        let req = request("GET", "api/v1/certificates")
        let (data, _) = try await perform(req)
        return try decode([CertDto].self, data)
    }
    struct GenerateCertBody: Encodable {
        let name: String; let commonName: String; let organization: String?
        let country: String?; let validityYears: Int; let setAsDefault: Bool
    }
    func generateCertificate(name: String, commonName: String, organization: String? = nil,
                             country: String? = nil, validityYears: Int = 3, setAsDefault: Bool = true) async throws -> CertDto {
        let body = try Self.jsonEncoder.encode(GenerateCertBody(
            name: name, commonName: commonName, organization: organization,
            country: country, validityYears: validityYears, setAsDefault: setAsDefault))
        let req = request("POST", "api/v1/certificates/generate", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(CertDto.self, data)
    }
    func setDefaultCertificate(_ id: UUID) async throws {
        let req = request("POST", "api/v1/certificates/\(id)/set-default")
        _ = try await perform(req)
    }
    func deleteCertificate(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/certificates/\(id)")
        _ = try await perform(req)
    }

    // MARK: - Share-Link erweitert mit vollen Optionen (v1.10.71)

    /// Same shape as createShareLink but exposes all optional fields
    /// (slug, password, download limit, expiry, message, notify). Used by
    /// the new "Freigabelink erstellen"-Sheet in iOS mit Web-parity.
    func createShareLinkFull(fileId: UUID? = nil, folderId: UUID? = nil,
                             slug: String? = nil, password: String? = nil,
                             maxDownloads: Int? = nil, expiresAt: Date? = nil,
                             message: String? = nil, notifyOnAccess: Bool = false) async throws -> ShareLinkDto {
        let body = try Self.jsonEncoder.encode(CreateShareLinkBody(
            fileId: fileId, folderId: folderId, slug: slug, password: password,
            maxDownloads: maxDownloads, expiresAt: expiresAt, message: message,
            notifyOnAccess: notifyOnAccess))
        let req = request("POST", "api/v1/links", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(ShareLinkDto.self, data)
    }

    // MARK: - Upload-Request mit vollen Optionen

    func createUploadRequestFull(slug: String? = nil, password: String? = nil,
                                 maxUploads: Int? = nil, expiresAt: Date? = nil,
                                 message: String? = nil, targetFolder: String = "Received",
                                 notifyOnUpload: Bool = true) async throws -> UploadRequestResult {
        let body = try Self.jsonEncoder.encode(CreateUploadRequestBody(
            slug: slug, password: password, maxUploads: maxUploads, expiresAt: expiresAt,
            message: message, targetFolder: targetFolder, notifyOnUpload: notifyOnUpload))
        let req = request("POST", "api/v1/upload-requests", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(UploadRequestResult.self, data)
    }

    // MARK: - Activity

    func activity(all: Bool = false, limit: Int = 100) async throws -> [ActivityDto] {
        let req = request("GET", "api/v1/activity", query: [
            .init(name: "all", value: all ? "true" : "false"),
            .init(name: "limit", value: String(limit)),
        ])
        let (data, _) = try await perform(req)
        return try decode([ActivityDto].self, data)
    }

    // MARK: - v1.10.82 App-Store-Blocker: Account-Löschung + UGC-Moderation

    struct DeleteAccountBody: Encodable { let password: String? }
    struct DeleteAccountResult: Decodable {
        let deleted: Bool
        let filesRemoved: Int?
        let bytesFreed: Int64?
        let blobDeleteFailures: Int?
    }
    func deleteMyAccount(password: String?) async throws -> DeleteAccountResult {
        let body = try Self.jsonEncoder.encode(DeleteAccountBody(password: password))
        let req = request("DELETE", "api/v1/me", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(DeleteAccountResult.self, data)
    }

    struct BlockedUserDto: Codable, Identifiable, Hashable {
        let id: UUID
        let blockedUserId: UUID
        let blockedName: String?
        let blockedEmail: String?
        let reason: String?
        let createdAt: Date
    }
    func listBlocks() async throws -> [BlockedUserDto] {
        let req = request("GET", "api/v1/moderation/blocks")
        let (data, _) = try await perform(req)
        return try decode([BlockedUserDto].self, data)
    }
    struct BlockBody: Encodable { let blockedUserId: UUID; let reason: String? }
    func blockUser(_ blockedUserId: UUID, reason: String? = nil) async throws {
        let body = try Self.jsonEncoder.encode(BlockBody(blockedUserId: blockedUserId, reason: reason))
        let req = request("POST", "api/v1/moderation/blocks", body: body, contentType: "application/json")
        _ = try await perform(req)
    }
    func unblockUser(_ blockedUserId: UUID) async throws {
        let req = request("DELETE", "api/v1/moderation/blocks/\(blockedUserId)")
        _ = try await perform(req)
    }

    // Reasons müssen mit dem C#-Enum ContentReportReason übereinstimmen.
    enum ReportReason: Int, Codable, CaseIterable, Identifiable {
        case spam = 0, harassment = 1, illegalContent = 2, intellectualProperty = 3
        case csamOrChildSafety = 4, impersonation = 5, malware = 6, other = 99
        var id: Int { rawValue }
        var germanLabel: String {
            switch self {
            case .spam: return "Spam"
            case .harassment: return "Belästigung / Hass"
            case .illegalContent: return "Rechtswidrige Inhalte"
            case .intellectualProperty: return "Urheberrechtsverletzung"
            case .csamOrChildSafety: return "Missbrauchsdarstellung / Kinderschutz"
            case .impersonation: return "Identitätsdiebstahl"
            case .malware: return "Malware / Phishing"
            case .other: return "Sonstiges"
            }
        }
    }
    enum ReportSubjectKind: Int, Codable {
        case file = 0, folder = 1, shareLink = 2, user = 3, contact = 4
        case signatureRequest = 5, wikiPage = 6, chatMessage = 7
    }
    struct ReportBody: Encodable {
        let subjectKind: Int
        let subjectId: UUID
        let reason: Int
        let note: String?
        let subjectLabel: String?
        let subjectOwnerUserId: UUID?
    }
    func reportContent(kind: ReportSubjectKind, subjectId: UUID, reason: ReportReason,
                       note: String? = nil, subjectLabel: String? = nil,
                       subjectOwnerUserId: UUID? = nil) async throws {
        let body = try Self.jsonEncoder.encode(ReportBody(
            subjectKind: kind.rawValue, subjectId: subjectId,
            reason: reason.rawValue, note: note,
            subjectLabel: subjectLabel, subjectOwnerUserId: subjectOwnerUserId))
        let req = request("POST", "api/v1/moderation/reports", body: body, contentType: "application/json")
        _ = try await perform(req)
    }

    // MARK: - v1.10.88 iOS-Parität: File-Locks, Wiki, API-Tokens, Webhooks, Email-Templates

    struct FileLockStatus: Decodable {
        let locked: Bool
        let byUserId: UUID?
        let byUserName: String?
        let until: Date?
        let kind: String?
    }
    func fileLockStatus(_ id: UUID) async throws -> FileLockStatus {
        let req = request("GET", "api/v1/files/\(id)/lock")
        let (data, _) = try await perform(req)
        return try decode(FileLockStatus.self, data)
    }
    func fileLockAcquire(_ id: UUID, kind: String = "manual") async throws {
        let req = request("POST", "api/v1/files/\(id)/lock", query: [.init(name: "kind", value: kind)])
        _ = try await perform(req)
    }
    func fileLockRelease(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/files/\(id)/lock")
        _ = try await perform(req)
    }

    // ── Wiki (mapped to server PageDto) ──
    struct WikiPageDto: Codable, Identifiable, Hashable {
        let id: UUID
        let scope: String
        let ownerUserId: UUID?
        let ownerGroupId: UUID?
        let parentPageId: UUID?
        let title: String
        let slug: String
        let contentMarkdown: String?
        let sortOrder: Int
        let createdByName: String
        let lastEditedByName: String?
        let createdAt: Date
        let updatedAt: Date
    }
    /// Scope: "Personal" | "Public" | "Group".
    func wikiPages(scope: String, groupId: UUID? = nil) async throws -> [WikiPageDto] {
        var q: [URLQueryItem] = [.init(name: "scope", value: scope)]
        if let g = groupId { q.append(.init(name: "groupId", value: g.uuidString)) }
        let req = request("GET", "api/v1/wiki/pages", query: q)
        let (data, _) = try await perform(req)
        return try decode([WikiPageDto].self, data)
    }
    func wikiPage(_ id: UUID) async throws -> WikiPageDto {
        let req = request("GET", "api/v1/wiki/pages/\(id)")
        let (data, _) = try await perform(req)
        return try decode(WikiPageDto.self, data)
    }

    // ── API-Tokens ──
    struct ApiTokenDto: Codable, Identifiable, Hashable {
        let id: UUID
        let name: String
        let prefix: String
        let scopes: String?
        let createdAt: Date
        let expiresAt: Date?
        let lastUsedAt: Date?
        let revokedAt: Date?
    }
    struct CreatedApiTokenDto: Codable {
        let token: ApiTokenDto
        let rawToken: String
    }
    struct CreateApiTokenBody: Encodable {
        let name: String; let scopes: String?; let expiresAt: Date?
    }
    func listApiTokens() async throws -> [ApiTokenDto] {
        let req = request("GET", "api/v1/dev/tokens")
        let (data, _) = try await perform(req)
        return try decode([ApiTokenDto].self, data)
    }
    func createApiToken(name: String, scopes: String?, expiresAt: Date?) async throws -> CreatedApiTokenDto {
        let body = try Self.jsonEncoder.encode(CreateApiTokenBody(name: name, scopes: scopes, expiresAt: expiresAt))
        let req = request("POST", "api/v1/dev/tokens", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(CreatedApiTokenDto.self, data)
    }
    func revokeApiToken(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/dev/tokens/\(id)")
        _ = try await perform(req)
    }

    // ── Webhooks ──
    struct WebhookDto: Codable, Identifiable, Hashable {
        let id: UUID
        let url: String
        let events: String?
        let isActive: Bool
        let createdAt: Date
        let lastDeliveredAt: Date?
        let failureCount: Int
    }
    struct CreateWebhookBody: Encodable {
        let url: String; let secret: String; let events: String?
    }
    func listWebhooks() async throws -> [WebhookDto] {
        let req = request("GET", "api/v1/dev/webhooks")
        let (data, _) = try await perform(req)
        return try decode([WebhookDto].self, data)
    }
    func createWebhook(url: String, secret: String, events: String?) async throws -> WebhookDto {
        let body = try Self.jsonEncoder.encode(CreateWebhookBody(url: url, secret: secret, events: events))
        let req = request("POST", "api/v1/dev/webhooks", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(WebhookDto.self, data)
    }
    func deleteWebhook(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/dev/webhooks/\(id)")
        _ = try await perform(req)
    }

    // ── Email-Templates (für Signatur-Wizard-Picker) ──
    struct EmailTemplateDto: Codable, Identifiable, Hashable {
        let id: UUID
        let name: String
        let kind: String
        let subject: String
        let bodyMarkdown: String
        let locale: String
        let isDefault: Bool
    }
    func listEmailTemplates(kind: String? = nil, locale: String? = nil) async throws -> [EmailTemplateDto] {
        var q: [URLQueryItem] = []
        if let k = kind { q.append(.init(name: "kind", value: k)) }
        if let l = locale { q.append(.init(name: "locale", value: l)) }
        let req = request("GET", "api/v1/email-templates", query: q)
        let (data, _) = try await perform(req)
        return try decode([EmailTemplateDto].self, data)
    }
}
