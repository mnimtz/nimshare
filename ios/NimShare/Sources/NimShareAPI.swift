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
        // v1.10.172: 20s Request-Timeout. Default ist 60s — bei unerreichbarem
        // Server steht die UI eine Minute lang auf ProgressView bevor irgendein
        // Fehler auftaucht. 20s deckt normale Latenz (auch KI-Endpoints, die
        // 5-15s brauchen können) ohne zu früh abzubrechen. Uploads nutzen einen
        // separaten Pfad (Blob-PUT) mit eigenem Timeout — hier nicht betroffen.
        req.timeoutInterval = 20
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        // v1.10.137: Sprache der App (Schnittmenge Gerät ∩ unterstützte
        // Sprachen) explizit mitschicken, damit serverseitige Inhalte wie die
        // KI-Begrüssung IMMER in derselben Sprache kommen wie die App-UI —
        // nicht nur nach dem, was URLSession vom Gerät ableitet.
        req.setValue(Bundle.main.preferredLocalizations.first ?? "de", forHTTPHeaderField: "Accept-Language")
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
        // v1.10.108: Cancellation NICHT in ApiError.network wrappen. Sonst
        // landet „Abgebrochen" (Task-Cancel bei Tab-Wechsel / Pull-Refresh)
        // als roter Fehler-Screen in jeder Listen-View — die Views können
        // CancellationError gezielt schlucken, ApiError.network nicht.
        catch is CancellationError { throw CancellationError() }
        catch let u as URLError where u.code == .cancelled { throw CancellationError() }
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

    // v1.11.18: x/y/width/height nachgerüstet — vorher konnte iOS nur den
    // Anchor-Preset ("BottomCenter" auf Seite 1) senden, nie eine exakte
    // Position. Web platziert per pdf.js-Drag-Overlay: x/y/width/height sind
    // PDF-Punkte (72dpi), Y GEMESSEN VON OBEN nach unten (nicht die native
    // PDF-Bottom-Left-Konvention — SignaturePdfService/XGraphics zeichnet
    // Top-Left-Y-runter, siehe Kommentar in NewRequest.cshtml `end()`).
    // SignatureFieldPlacementView rendert die Seite als Bild bei bekannter
    // Punkt-Skalierung und rechnet Drag-Pixel direkt in dieses System um —
    // keine zusätzliche Flip-Logik nötig.
    struct AddFieldBody: Encodable {
        let participantId: UUID; let type: String; let page: Int; let anchor: String; let label: String?
        let x: Double?; let y: Double?; let width: Double?; let height: Double?
        init(participantId: UUID, type: String, page: Int, anchor: String, label: String? = nil,
             x: Double? = nil, y: Double? = nil, width: Double? = nil, height: Double? = nil) {
            self.participantId = participantId
            self.type = type
            self.page = page
            self.anchor = anchor
            self.label = label
            self.x = x; self.y = y; self.width = width; self.height = height
        }
    }
    @discardableResult
    func addSignatureField(_ requestId: UUID, participantId: UUID, type: String = "Signature",
                           page: Int = 1, anchor: String = "BottomCenter", label: String? = nil,
                           x: Double? = nil, y: Double? = nil, width: Double? = nil, height: Double? = nil) async throws -> UUID {
        let body = try Self.jsonEncoder.encode(AddFieldBody(participantId: participantId, type: type, page: page, anchor: anchor, label: label,
                                                              x: x, y: y, width: width, height: height))
        let req = request("POST", "api/v1/signatures/\(requestId)/fields", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        struct R: Decodable { let id: UUID }
        return try decode(R.self, data).id
    }
    // v1.11.18: bisher nie aufgerufen — Server bietet DELETE seit v1.10.146
    // (Undo für ein mis-platziertes Feld), iOS hatte keine UI dafür.
    func removeSignatureField(_ requestId: UUID, fieldId: UUID) async throws {
        let req = request("DELETE", "api/v1/signatures/\(requestId)/fields/\(fieldId)")
        _ = try await perform(req)
    }

    // v1.11.47: templateId optional — wählt die Email-Vorlage (Betreff/Text
    // der Einladungs-Mail), analog Web's /send?templateId=. Bewusst getrennt
    // von SignatureRequest.Title/Message (das ist der Text, den der
    // Unterzeichner auf der Landing sieht, nicht der Mail-Inhalt).
    func sendSignatureRequest(_ id: UUID, templateId: UUID? = nil) async throws -> SignatureRequestDto {
        var query: [URLQueryItem] = []
        if let templateId { query.append(.init(name: "templateId", value: templateId.uuidString)) }
        let req = request("POST", "api/v1/signatures/\(id)/send", query: query)
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

    /// v1.10.195: Authentifizierter Request für das Thumbnail-Endpoint.
    /// Der Endpoint ist ApiUser-geschützt (Bearer nötig) und antwortet mit
    /// 302-Redirect auf eine Azure-SAS-URL. Der ThumbLoader führt den
    /// Request mit einer Session aus, die beim Redirect den Authorization-
    /// Header strippt (Azure lehnt SAS + fremden Auth-Header sonst ab).
    func thumbnailRequest(fileId: UUID, size: Int = 400) -> URLRequest {
        request("GET", "api/v1/files/\(fileId)/thumb",
                query: [.init(name: "size", value: String(size))])
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

    // MARK: - AI Consent (Apple 5.1.1(i))
    // v1.10.165: iOS holt sich Provider-Info + User-Consent-State, damit der
    // First-Use-Dialog konkret sagen kann wer der Empfänger ist, und der
    // Consent server-seitig persistiert wird.
    struct AiProviderInfo: Codable {
        let provider: String
        let model: String?
        let endpointHint: String?
        let chatWithFilesEnabled: Bool
        let autoSummaryEnabled: Bool
        let smartTagsEnabled: Bool
        let ocrEnabled: Bool
        let enabled: Bool
    }
    struct AiConsentDto: Codable {
        let consented: Bool
        let consentedAt: Date?
    }
    struct SetAiConsentBody: Codable { let consented: Bool }

    func aiProviderInfo() async throws -> AiProviderInfo {
        let req = request("GET", "api/v1/ai/provider-info")
        let (data, _) = try await perform(req)
        return try decode(AiProviderInfo.self, data)
    }
    func aiConsent() async throws -> AiConsentDto {
        let req = request("GET", "api/v1/me/ai-consent")
        let (data, _) = try await perform(req)
        return try decode(AiConsentDto.self, data)
    }
    func setAiConsent(_ granted: Bool) async throws -> AiConsentDto {
        let body = try Self.jsonEncoder.encode(SetAiConsentBody(consented: granted))
        let req = request("PUT", "api/v1/me/ai-consent", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(AiConsentDto.self, data)
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

    // MARK: - v1.10.167 Gallery-Ordner-Typ
    /// Regular = klassischer Datei-Browser · Gallery = Foto/Video-Album mit
    /// optimierter Landing (Grid + Lightbox) und optionalem Upload-Widget.
    enum FolderKind: String, Codable { case regular = "Regular", gallery = "Gallery" }
    struct SetFolderKindBody: Encodable { let kind: String }
    struct SetFolderKindResponse: Decodable { let id: UUID; let kind: String }
    func setFolderKind(id: UUID, kind: FolderKind) async throws -> FolderKind {
        let body = try Self.jsonEncoder.encode(SetFolderKindBody(kind: kind.rawValue))
        let req = request("PATCH", "api/v1/folders/\(id)/kind", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        let parsed = try decode(SetFolderKindResponse.self, data)
        return FolderKind(rawValue: parsed.kind) ?? .regular
    }

    func sharedWithMe() async throws -> [SharedWithMeItemDto] {
        let req = request("GET", "api/v1/direct-shares/shared-with-me")
        let (data, _) = try await perform(req)
        return try decode([SharedWithMeItemDto].self, data)
    }

    // MARK: - Share-Link / Upload-Request (v1.10.66 iOS parity)

    // v1.11.0: Subdomain-Sharing. Info einmal pro Sheet laden (Feature an?
    // Basis-Domain? Darf DIESER User es nutzen?), Check live pro Tastendruck
    // (debounced im Sheet).
    struct SubdomainInfo: Decodable {
        let enabled: Bool
        let baseDomain: String?
        let canUse: Bool
    }
    func subdomainInfo() async throws -> SubdomainInfo {
        let req = request("GET", "api/v1/links/subdomain-info")
        let (data, _) = try await perform(req)
        return try decode(SubdomainInfo.self, data)
    }

    struct SubdomainCheck: Decodable {
        let available: Bool
        let reason: String?
        let normalised: String
    }
    func subdomainCheck(_ slug: String) async throws -> SubdomainCheck {
        let req = request("GET", "api/v1/links/subdomain-check",
                          query: [.init(name: "slug", value: slug)])
        let (data, _) = try await perform(req)
        return try decode(SubdomainCheck.self, data)
    }

    struct CreateShareLinkBody: Encodable {
        let fileId: UUID?
        let folderId: UUID?
        let slug: String?
        let password: String?
        let maxDownloads: Int?
        let expiresAt: Date?
        let message: String?
        let notifyOnAccess: Bool
        // v1.10.146: optionales Absender-Zertifikat.
        let signingCertificateId: UUID?
        // v1.10.167: Landing als Foto/Video-Album rendern (Grid + Lightbox).
        let displayAsGallery: Bool?
        // v1.10.167: „Upload erlauben" — nur wirksam wenn displayAsGallery
        // ODER Folder.Kind==Gallery. Server enforced.
        let allowUploads: Bool?
        // v1.11.0: Link zusätzlich als Subdomain (slug.base.tld) freigeben.
        let subdomainSlug: String?
        // v1.11.18: optionale Seriennummer/Lizenzcode — Server verschlüsselt,
        // Landing zeigt sie erst nach Klick.
        let serialNumber: String?
        // v1.11.50: explizites "läuft nie ab" — Default false, damit ein
        // fehlendes expiresAt serverseitig auf +8 Wochen defaultet.
        let isPermanent: Bool

        // v1.10.169: expliziter init mit Defaults. Swift's synthesized
        // memberwise init verschluckt sich an inline `= nil`-Defaults auf
        // Optional-Properties in `Encodable`-structs — Xcode meldete „Extra
        // arguments at positions #10, #11 in call". Der explizite init macht
        // die Defaultbarkeit unmissverständlich für alle Aufrufer.
        init(fileId: UUID?, folderId: UUID?, slug: String?, password: String?,
             maxDownloads: Int?, expiresAt: Date?, message: String?,
             notifyOnAccess: Bool, signingCertificateId: UUID?,
             displayAsGallery: Bool? = nil, allowUploads: Bool? = nil,
             subdomainSlug: String? = nil, serialNumber: String? = nil,
             isPermanent: Bool = false) {
            self.fileId = fileId
            self.folderId = folderId
            self.slug = slug
            self.password = password
            self.maxDownloads = maxDownloads
            self.expiresAt = expiresAt
            self.message = message
            self.notifyOnAccess = notifyOnAccess
            self.signingCertificateId = signingCertificateId
            self.displayAsGallery = displayAsGallery
            self.allowUploads = allowUploads
            self.subdomainSlug = subdomainSlug
            self.serialNumber = serialNumber
            self.isPermanent = isPermanent
        }
    }
    /// Create a share link with default options (no password, no expiry, no
    /// download limit). Returns the freshly created ShareLinkDto — caller
    /// pastes/shows the .url. v1.10.146: optional signing certificate.
    func createShareLink(fileId: UUID? = nil, folderId: UUID? = nil,
                         signingCertificateId: UUID? = nil,
                         displayAsGallery: Bool? = nil,
                         allowUploads: Bool? = nil) async throws -> ShareLinkDto {
        let body = try Self.jsonEncoder.encode(CreateShareLinkBody(
            fileId: fileId, folderId: folderId, slug: nil, password: nil,
            maxDownloads: nil, expiresAt: nil, message: nil, notifyOnAccess: false,
            signingCertificateId: signingCertificateId,
            displayAsGallery: displayAsGallery, allowUploads: allowUploads))
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
        // v1.10.146: optionales Absender-Zertifikat.
        let signingCertificateId: UUID?
        // v1.11.0: Anforderung zusätzlich als Subdomain freigeben.
        let subdomainSlug: String?
        // v1.11.50: explizites "läuft nie ab" — Default false, damit ein
        // fehlendes expiresAt serverseitig auf +8 Wochen defaultet.
        let isPermanent: Bool
    }
    struct UploadRequestResult: Decodable {
        let id: UUID
        let slug: String
        let url: String
        // v1.11.0: optional, damit alte Server ohne das Feld weiter dekodieren.
        let subdomainUrl: String?
    }
    /// Create an upload-request link (reverse-share). Uploaded files land in
    /// the owner's Personal → "Received" folder (server default).
    /// v1.10.146: optional signing certificate.
    func createUploadRequest(message: String? = nil,
                             signingCertificateId: UUID? = nil) async throws -> UploadRequestResult {
        let body = try Self.jsonEncoder.encode(CreateUploadRequestBody(
            slug: nil, password: nil, maxUploads: nil, expiresAt: nil,
            message: message, targetFolder: "Received", notifyOnUpload: true,
            signingCertificateId: signingCertificateId, subdomainSlug: nil,
            isPermanent: false))
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

    // v1.10.113: Ordner verschieben/kopieren/löschen (Web-Parität).
    func moveFolder(id: UUID, targetFolderId: UUID) async throws {
        let body = try Self.jsonEncoder.encode(MoveFileBody(folderId: targetFolderId))
        let req = request("POST", "api/v1/folders/\(id)/move", body: body, contentType: "application/json")
        _ = try await perform(req)
    }
    func copyFolder(id: UUID, targetFolderId: UUID) async throws {
        let body = try Self.jsonEncoder.encode(MoveFileBody(folderId: targetFolderId))
        let req = request("POST", "api/v1/folders/\(id)/copy", body: body, contentType: "application/json")
        _ = try await perform(req)
    }
    func deleteFolder(id: UUID, force: Bool = true) async throws {
        let req = request("DELETE", "api/v1/folders/\(id)", query: [.init(name: "force", value: force ? "true" : "false")])
        _ = try await perform(req)
    }
    // v1.10.113: Share-Link (Meine Links) löschen.
    func deleteShareLink(id: UUID) async throws {
        let req = request("DELETE", "api/v1/links/\(id)")
        _ = try await perform(req)
    }

    // v1.11.18: PATCH-Update für Share-Links — vorher rief iOS nie diesen
    // Endpoint auf, obwohl der Server ihn seit v1.10.x anbietet (Web nutzt ihn
    // für Widerrufen, Public-Toggle, AllowedEmails). Erster Konsument hier:
    // "Widerrufen" als eigene Aktion getrennt von "Löschen" in LinksView.
    struct UpdateShareLinkBody: Encodable {
        var isRevoked: Bool?
        // v1.11.19: isPublic dazu — Admin-Toggle "öffentlich kuratiert
        // machen", analog Web (Links.cshtml data-toggle-public, nur bei
        // Admin-Rolle sichtbar; Server ignoriert das Feld sonst still).
        var isPublic: Bool?
        init(isRevoked: Bool? = nil, isPublic: Bool? = nil) {
            self.isRevoked = isRevoked
            self.isPublic = isPublic
        }
    }
    func updateShareLink(id: UUID, isRevoked: Bool? = nil, isPublic: Bool? = nil) async throws -> ShareLinkDto {
        let body = try Self.jsonEncoder.encode(UpdateShareLinkBody(isRevoked: isRevoked, isPublic: isPublic))
        let req = request("PATCH", "api/v1/links/\(id)", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(ShareLinkDto.self, data)
    }

    // v1.11.19: "Per E-Mail senden" — Server-Endpoint existiert seit v1.10.x
    // (LinksController.SendByEmail), iOS rief ihn nie auf.
    struct SendLinkByEmailBody: Encodable { let toEmail: String; let message: String? }
    func sendShareLinkByEmail(id: UUID, toEmail: String, message: String? = nil) async throws {
        let body = try Self.jsonEncoder.encode(SendLinkByEmailBody(toEmail: toEmail, message: message))
        let req = request("POST", "api/v1/links/\(id)/send-email", body: body, contentType: "application/json")
        _ = try await perform(req)
    }

    // v1.10.158: Link-Report — reiche Aggregate, für die LinkReportView.
    struct LinkReportCountRow: Codable, Hashable {
        let key: String
        let count: Int
    }
    struct LinkReportDailyRow: Codable, Hashable {
        let day: String   // ISO date "yyyy-MM-dd"
        let landings: Int
        let downloads: Int
        let passwordFails: Int
    }
    struct LinkReportHeatCell: Codable, Hashable {
        let dayOfWeek: Int
        let hour: Int
        let count: Int
    }
    struct LinkReportEvent: Codable, Hashable {
        let at: Date
        let kind: String
        let countryCode: String?
        let city: String?
        let deviceType: String?
        let timezone: String?
        let referer: String?
        let ipAddress: String?
    }
    struct LinkReportResponse: Codable {
        let linkId: UUID
        let slug: String
        let hitCount: Int
        let downloadCount: Int
        let uniqueVisitors: Int
        let medianTimeToDownloadSeconds: Double?
        let lastAccessAt: Date?
        let byDay: [LinkReportDailyRow]
        let countries: [LinkReportCountRow]
        let cities: [LinkReportCountRow]
        let devices: [LinkReportCountRow]
        let timezones: [LinkReportCountRow]
        let referrers: [LinkReportCountRow]
        let hourHeatmap: [LinkReportHeatCell]
        let recentEvents: [LinkReportEvent]
        let totalEventCount: Int
        let storeFullIp: Bool
    }
    func linkReport(id: UUID) async throws -> LinkReportResponse {
        let req = request("GET", "api/v1/links/\(id)/report")
        let (data, _) = try await perform(req)
        return try decode(LinkReportResponse.self, data)
    }

    // v1.10.114: KI-Startseiten-Begrüssung (optional mit Standort fürs Wetter).
    // v1.10.128: Anrede + Nachricht getrennt für ordentliche Formatierung.
    struct GreetingResponse: Decodable {
        let greeting: String
        let salutation: String?
        let body: String?
    }
    struct Greeting { let salutation: String; let message: String }
    func greeting(lat: Double? = nil, lon: Double? = nil) async throws -> Greeting {
        var q: [URLQueryItem] = []
        if let la = lat, let lo = lon {
            q.append(.init(name: "lat", value: String(la)))
            q.append(.init(name: "lon", value: String(lo)))
        }
        let req = request("GET", "api/v1/ai/greeting", query: q)
        let (data, _) = try await perform(req)
        let r = try decode(GreetingResponse.self, data)
        // Neuer Server liefert Anrede + Nachricht getrennt.
        if let s = r.salutation, let b = r.body, !s.isEmpty {
            return Greeting(salutation: s, message: b)
        }
        // Älterer Server liefert nur den vollen Text — clientseitig in Anrede
        // (bis zum ersten ! oder .) und Nachricht splitten, damit die zwei-
        // zeilige Formatierung auch ohne Server-Update greift.
        return Self.split(r.greeting)
    }

    static func split(_ full: String) -> Greeting {
        let t = full.trimmingCharacters(in: .whitespacesAndNewlines)
        // Erste Satzgrenze suchen: "! " oder ". "
        if let r = t.range(of: "! ") ?? t.range(of: ". ") {
            let sal = String(t[..<r.lowerBound]).trimmingCharacters(in: .whitespaces)
            let msg = String(t[r.upperBound...]).trimmingCharacters(in: .whitespaces)
            if !sal.isEmpty, !msg.isEmpty {
                // Anrede sauber mit Komma abschliessen.
                let salComma = sal.hasSuffix(",") ? sal : sal + ","
                return Greeting(salutation: salComma, message: msg)
            }
        }
        return Greeting(salutation: "", message: t)
    }

    // v1.10.122: Wetter-Symbol + heutige Vorhersage fürs Nav-Symbol.
    struct WeatherInfo: Decodable {
        let tempC: Int
        let highC: Int
        let lowC: Int
        let code: Int
        let text: String
        let emoji: String
        let sfSymbol: String
    }
    func weather(lat: Double, lon: Double) async throws -> WeatherInfo {
        let q: [URLQueryItem] = [
            .init(name: "lat", value: String(lat)),
            .init(name: "lon", value: String(lon))
        ]
        let req = request("GET", "api/v1/ai/weather", query: q)
        let (data, _) = try await perform(req)
        return try decode(WeatherInfo.self, data)
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
    /// Behält den Data-Pfad für kleine, in-memory erzeugte Blobs (z.B.
    /// PencilKit-Signaturen). Große Files (PDFs aus dem Files-Picker) sollten
    /// den `fromFile:`-Overload nutzen, der ohne RAM-Verdopplung streamt.
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

    /// v1.10.150: Streaming-Upload direkt aus einer lokalen Datei — hält die
    /// PDF NIEMALS komplett im RAM. URLSession streamt aus dem FS zum Azure-
    /// Endpoint. Für 100 MB+ Dateien der einzig sichere Pfad; kleinere Files
    /// dürfen weiter den Data-Overload nutzen.
    func uploadFile(name: String, contentType: String, folderId: UUID?, fromFile fileURL: URL) async throws -> UUID {
        let attrs = try FileManager.default.attributesOfItem(atPath: fileURL.path)
        let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
        let body = try Self.jsonEncoder.encode(InitUploadBody(
            name: name, sizeBytes: size, contentType: contentType, folderId: folderId))
        let initReq = request("POST", "api/v1/files", body: body, contentType: "application/json")
        let (initData, _) = try await perform(initReq)
        let init_ = try decode(InitUploadResp.self, initData)

        guard let url = URL(string: init_.uploadUrl) else { throw ApiError.network("Bad upload URL") }
        var putReq = URLRequest(url: url)
        putReq.httpMethod = init_.uploadMethod ?? "PUT"
        putReq.setValue("BlockBlob", forHTTPHeaderField: "x-ms-blob-type")
        putReq.setValue(contentType, forHTTPHeaderField: "Content-Type")
        let (_, putResp) = try await URLSession.shared.upload(for: putReq, fromFile: fileURL)
        if let http = putResp as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw ApiError.http(http.statusCode, "Azure Blob upload failed")
        }

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
    // v1.10.113: Kontakt bearbeiten (Long-Press → Bearbeiten).
    @discardableResult
    func updateContact(id: UUID, email: String, name: String, company: String? = nil,
                       notes: String? = nil, tags: String? = nil) async throws -> ContactDto {
        let body = try Self.jsonEncoder.encode(CreateContactBody(email: email, name: name, company: company, notes: notes, tags: tags))
        let req = request("PATCH", "api/v1/contacts/\(id)", body: body, contentType: "application/json")
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
                             message: String? = nil, notifyOnAccess: Bool = false,
                             signingCertificateId: UUID? = nil,
                             displayAsGallery: Bool? = nil,
                             allowUploads: Bool? = nil,
                             subdomainSlug: String? = nil,
                             serialNumber: String? = nil,
                             isPermanent: Bool = false) async throws -> ShareLinkDto {
        let body = try Self.jsonEncoder.encode(CreateShareLinkBody(
            fileId: fileId, folderId: folderId, slug: slug, password: password,
            maxDownloads: maxDownloads, expiresAt: expiresAt, message: message,
            notifyOnAccess: notifyOnAccess, signingCertificateId: signingCertificateId,
            displayAsGallery: displayAsGallery, allowUploads: allowUploads,
            subdomainSlug: subdomainSlug, serialNumber: serialNumber,
            isPermanent: isPermanent))
        let req = request("POST", "api/v1/links", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(ShareLinkDto.self, data)
    }

    // MARK: - Upload-Request mit vollen Optionen

    func createUploadRequestFull(slug: String? = nil, password: String? = nil,
                                 maxUploads: Int? = nil, expiresAt: Date? = nil,
                                 message: String? = nil, targetFolder: String = "Received",
                                 notifyOnUpload: Bool = true,
                                 signingCertificateId: UUID? = nil,
                                 subdomainSlug: String? = nil,
                                 isPermanent: Bool = false) async throws -> UploadRequestResult {
        let body = try Self.jsonEncoder.encode(CreateUploadRequestBody(
            slug: slug, password: password, maxUploads: maxUploads, expiresAt: expiresAt,
            message: message, targetFolder: targetFolder, notifyOnUpload: notifyOnUpload,
            signingCertificateId: signingCertificateId, subdomainSlug: subdomainSlug,
            isPermanent: isPermanent))
        let req = request("POST", "api/v1/upload-requests", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(UploadRequestResult.self, data)
    }

    // v1.10.147: Upload-Requests LISTEN + widerrufen — Server-Endpoints
    // GET/DELETE /api/v1/upload-requests existieren seit v1.7, iOS rief sie
    // aber nie an. User erstellte URL, vergaß den Slug, konnte sie nicht
    // mehr einsehen/löschen ohne Web.
    struct UploadRequestListItemDto: Decodable, Identifiable {
        let id: UUID
        let slug: String
        let createdAt: Date
        let expiresAt: Date?
        // v1.11.50: explizites "läuft nie ab".
        let isPermanent: Bool
        let maxUploads: Int?
        let uploadCount: Int
        let isRevoked: Bool
        let targetFolder: String?
    }
    func listUploadRequests() async throws -> [UploadRequestListItemDto] {
        let req = request("GET", "api/v1/upload-requests")
        let (data, _) = try await perform(req)
        return try decode([UploadRequestListItemDto].self, data)
    }
    func deleteUploadRequest(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/upload-requests/\(id)")
        _ = try await perform(req)
    }

    // v1.10.148: Bug #7 — Ordner per Id browsen (Für SharedWithMeView-Tap
    // auf Ordner). Server-Endpoint /api/v1/folders/{id}/browse.
    struct FolderBrowseResponse: Decodable {
        struct Sub: Decodable, Identifiable { let id: UUID; let name: String }
        struct FileRow: Decodable, Identifiable {
            let id: UUID; let name: String; let sizeBytes: Int64
            let contentType: String; let createdAt: Date
        }
        let id: UUID
        let name: String
        let scope: String
        let subfolders: [Sub]
        let files: [FileRow]
    }
    func browseFolder(_ id: UUID) async throws -> FolderBrowseResponse {
        let req = request("GET", "api/v1/folders/\(id)/browse")
        let (data, _) = try await perform(req)
        return try decode(FolderBrowseResponse.self, data)
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

    // ── v1.10.111: Linksammlung (löst Wiki ab) ──
    // Eine geteilte, flache Liste. Alle sehen sie, nur Admins pflegen.
    struct LinkEntryDto: Codable, Identifiable, Hashable {
        let id: UUID
        let title: String
        let url: String
        let description: String?
        let emoji: String?
        let sortOrder: Int
    }
    func linkCollection() async throws -> [LinkEntryDto] {
        let req = request("GET", "api/v1/link-collection")
        let (data, _) = try await perform(req)
        return try decode([LinkEntryDto].self, data)
    }
    struct LinkEntryBody: Encodable {
        let title: String
        let url: String
        let description: String?
        let emoji: String?
    }
    @discardableResult
    func createLink(title: String, url: String, description: String?, emoji: String?) async throws -> LinkEntryDto {
        let body = try Self.jsonEncoder.encode(LinkEntryBody(title: title, url: url, description: description, emoji: emoji))
        let req = request("POST", "api/v1/link-collection", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(LinkEntryDto.self, data)
    }
    @discardableResult
    func updateLink(id: UUID, title: String, url: String, description: String?, emoji: String?) async throws -> LinkEntryDto {
        let body = try Self.jsonEncoder.encode(LinkEntryBody(title: title, url: url, description: description, emoji: emoji))
        let req = request("PUT", "api/v1/link-collection/\(id)", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(LinkEntryDto.self, data)
    }
    func deleteLink(id: UUID) async throws {
        let req = request("DELETE", "api/v1/link-collection/\(id)")
        _ = try await perform(req)
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

    // ── Key-Store (v1.11.39: iOS-Parität) ──
    struct KeyStoreEntryDto: Codable, Identifiable, Hashable {
        let id: UUID
        let customerName: String
        let customerEmail: String?
        let customerEmailDomain: String?
        let keyType: String
        let validFrom: Date?
        let validUntil: Date?
        let notes: String?
        let createdAt: Date
        let updatedAt: Date?
        let ownerName: String?
        let isOwnedByMe: Bool
    }
    struct KeyStoreRevealDto: Codable { let keyValue: String }
    struct CreateKeyStoreEntryBody: Encodable {
        let customerName: String
        let customerEmail: String?
        let customerEmailDomain: String?
        let keyType: String
        let keyValue: String
        let validFrom: Date?
        let validUntil: Date?
        let notes: String?
    }
    struct UpdateKeyStoreEntryBody: Encodable {
        let customerName: String?
        let customerEmail: String?
        let customerEmailDomain: String?
        let keyType: String?
        let keyValue: String?
        let validFrom: Date?
        let validUntil: Date?
        let notes: String?
        let clearValidFrom: Bool
        let clearValidUntil: Bool
    }
    func listKeyStoreEntries(q: String? = nil) async throws -> [KeyStoreEntryDto] {
        var query: [URLQueryItem] = []
        if let q, !q.isEmpty { query.append(.init(name: "q", value: q)) }
        let req = request("GET", "api/v1/keystore", query: query)
        let (data, _) = try await perform(req)
        return try decode([KeyStoreEntryDto].self, data)
    }
    func revealKeyStoreEntry(_ id: UUID) async throws -> KeyStoreRevealDto {
        let req = request("GET", "api/v1/keystore/\(id)/reveal")
        let (data, _) = try await perform(req)
        return try decode(KeyStoreRevealDto.self, data)
    }
    func createKeyStoreEntry(_ body: CreateKeyStoreEntryBody) async throws -> KeyStoreEntryDto {
        let data = try Self.jsonEncoder.encode(body)
        let req = request("POST", "api/v1/keystore", body: data, contentType: "application/json")
        let (respData, _) = try await perform(req)
        return try decode(KeyStoreEntryDto.self, respData)
    }
    func updateKeyStoreEntry(_ id: UUID, _ body: UpdateKeyStoreEntryBody) async throws -> KeyStoreEntryDto {
        let data = try Self.jsonEncoder.encode(body)
        let req = request("PATCH", "api/v1/keystore/\(id)", body: data, contentType: "application/json")
        let (respData, _) = try await perform(req)
        return try decode(KeyStoreEntryDto.self, respData)
    }
    func deleteKeyStoreEntry(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/keystore/\(id)")
        _ = try await perform(req)
    }

    // ── Key-Store Documents (v1.11.37 auf dem Server, hier nachgezogen) ──
    struct KeyStoreDocumentDto: Codable, Identifiable, Hashable {
        let id: UUID
        let label: String
        let isFile: Bool
        let fileName: String?
        let url: String?
        let keyTypes: [String]
        let createdAt: Date
        let updatedAt: Date?
        let ownerName: String?
        let isOwnedByMe: Bool
    }
    struct CreateKeyStoreLinkDocBody: Encodable {
        let label: String
        let url: String
        let keyTypes: [String]
    }
    struct UpdateKeyStoreDocBody: Encodable {
        let label: String?
        let url: String?
        let keyTypes: [String]?
    }
    func listKeyStoreDocuments() async throws -> [KeyStoreDocumentDto] {
        let req = request("GET", "api/v1/keystore/documents")
        let (data, _) = try await perform(req)
        return try decode([KeyStoreDocumentDto].self, data)
    }
    func createKeyStoreLinkDocument(label: String, url: String, keyTypes: [String]) async throws -> KeyStoreDocumentDto {
        let data = try Self.jsonEncoder.encode(CreateKeyStoreLinkDocBody(label: label, url: url, keyTypes: keyTypes))
        let req = request("POST", "api/v1/keystore/documents/link", body: data, contentType: "application/json")
        let (respData, _) = try await perform(req)
        return try decode(KeyStoreDocumentDto.self, respData)
    }
    /// multipart/form-data — der Server-Endpoint nimmt IFormFile, kein SAS-
    /// Zweiphasen-Upload wie bei normalen Dateien (kleine PDFs, admin-seitig).
    private func multipartFileBody(fields: [(String, String)], fileField: String, fileData: Data, fileName: String, mimeType: String, boundary: String) -> Data {
        var body = Data()
        for (k, v) in fields {
            body.append("--\(boundary)\r\n".data(using: .utf8)!)
            body.append("Content-Disposition: form-data; name=\"\(k)\"\r\n\r\n".data(using: .utf8)!)
            body.append("\(v)\r\n".data(using: .utf8)!)
        }
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"\(fileField)\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: \(mimeType)\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        return body
    }
    func uploadKeyStoreDocument(label: String, keyTypes: [String], fileData: Data, fileName: String, mimeType: String) async throws -> KeyStoreDocumentDto {
        let boundary = "Boundary-\(UUID().uuidString)"
        let body = multipartFileBody(
            fields: [("label", label), ("keyTypes", keyTypes.joined(separator: ","))],
            fileField: "file", fileData: fileData, fileName: fileName, mimeType: mimeType, boundary: boundary)
        let req = request("POST", "api/v1/keystore/documents/upload", body: body, contentType: "multipart/form-data; boundary=\(boundary)")
        let (respData, _) = try await perform(req)
        return try decode(KeyStoreDocumentDto.self, respData)
    }
    func replaceKeyStoreDocumentFile(_ id: UUID, fileData: Data, fileName: String, mimeType: String) async throws -> KeyStoreDocumentDto {
        let boundary = "Boundary-\(UUID().uuidString)"
        let body = multipartFileBody(
            fields: [], fileField: "file", fileData: fileData, fileName: fileName, mimeType: mimeType, boundary: boundary)
        let req = request("POST", "api/v1/keystore/documents/\(id)/replace-file", body: body, contentType: "multipart/form-data; boundary=\(boundary)")
        let (respData, _) = try await perform(req)
        return try decode(KeyStoreDocumentDto.self, respData)
    }
    func updateKeyStoreDocument(_ id: UUID, label: String? = nil, url: String? = nil, keyTypes: [String]? = nil) async throws -> KeyStoreDocumentDto {
        let data = try Self.jsonEncoder.encode(UpdateKeyStoreDocBody(label: label, url: url, keyTypes: keyTypes))
        let req = request("PATCH", "api/v1/keystore/documents/\(id)", body: data, contentType: "application/json")
        let (respData, _) = try await perform(req)
        return try decode(KeyStoreDocumentDto.self, respData)
    }
    func deleteKeyStoreDocument(_ id: UUID) async throws {
        let req = request("DELETE", "api/v1/keystore/documents/\(id)")
        _ = try await perform(req)
    }
}
