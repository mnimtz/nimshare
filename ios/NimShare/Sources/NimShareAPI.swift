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
    func login(email: String, password: String) async throws -> LoginResponse {
        let body = try Self.jsonEncoder.encode(LoginBody(email: email, password: password))
        let req = request("POST", "api/v1/auth/login", body: body, contentType: "application/json")
        let (data, _) = try await perform(req)
        return try decode(LoginResponse.self, data)
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
        // Endpoint may wrap in {items:[]} or return array — try both.
        if let arr = try? Self.jsonDecoder.decode([ShareLinkDto].self, from: data) { return arr }
        struct Wrapper: Decodable { let items: [ShareLinkDto] }
        return (try? Self.jsonDecoder.decode(Wrapper.self, from: data).items) ?? []
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
}
