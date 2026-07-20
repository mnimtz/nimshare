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
    }
    func createSignatureRequest(sourceFileId: UUID, title: String? = nil, message: String? = nil,
                                deliveryOrder: String = "Parallel") async throws -> SignatureRequestDto {
        let body = try Self.jsonEncoder.encode(CreateSignatureBody(sourceFileId: sourceFileId, title: title, message: message, deliveryOrder: deliveryOrder))
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

    func sharedWithMe() async throws -> [SharedWithMeItemDto] {
        let req = request("GET", "api/v1/direct-shares/shared-with-me")
        let (data, _) = try await perform(req)
        return try decode([SharedWithMeItemDto].self, data)
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
}
