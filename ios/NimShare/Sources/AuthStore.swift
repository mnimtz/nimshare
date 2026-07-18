import Foundation
import SwiftUI

enum AuthState {
    case booting
    case needsServer
    case needsLogin
    case signedIn
}

@MainActor
final class AuthStore: ObservableObject {
    @Published var state: AuthState = .booting
    @Published var user: UserDto?
    @Published var serverURL: URL?
    @Published var api: NimShareAPI?

    private let defaults = UserDefaults.standard
    private let serverURLKey = "nimshare.serverURL"
    private let tokenKey = "nimshare.jwt"

    func bootstrap() async {
        if let raw = defaults.string(forKey: serverURLKey), let url = URL(string: raw) {
            serverURL = url
            let token = Keychain.get(tokenKey)
            api = NimShareAPI(baseURL: url, token: token)
            if token != nil {
                do {
                    let me = try await api!.me()
                    user = me
                    state = .signedIn
                    return
                } catch {
                    // token expired / revoked
                    Keychain.remove(forKey: tokenKey)
                    api?.setToken(nil)
                    state = .needsLogin
                    return
                }
            }
            state = .needsLogin
        } else {
            state = .needsServer
        }
    }

    func setServer(_ url: URL) {
        serverURL = url
        defaults.set(url.absoluteString, forKey: serverURLKey)
        api = NimShareAPI(baseURL: url)
        state = .needsLogin
    }

    func login(email: String, password: String) async throws {
        guard let api else { throw ApiError.network("No server") }
        let resp = try await api.login(email: email, password: password)
        Keychain.set(resp.token, forKey: tokenKey)
        api.setToken(resp.token)
        user = resp.user
        state = .signedIn
    }

    func signOut() {
        Keychain.remove(forKey: tokenKey)
        api?.setToken(nil)
        user = nil
        state = .needsLogin
    }

    func changeServer() {
        signOut()
        defaults.removeObject(forKey: serverURLKey)
        serverURL = nil
        api = nil
        state = .needsServer
    }
}
