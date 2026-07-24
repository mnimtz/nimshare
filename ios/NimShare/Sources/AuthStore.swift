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
    private let lastEmailKey = "nimshare.lastEmail"
    private let rememberKey = "nimshare.rememberCredentials"

    /// v1.10.59: Werksseitig eingestellte Standard-URL. Marcus's Vorgabe.
    /// User kann via "Server ändern" trotzdem umschalten wenn nötig.
    static let defaultServerURL = URL(string: "https://nimshare.com")!

    /// v1.10.59: letzter erfolgreicher Login — für "Email merken" auf der
    /// Login-Seite. Passwort läuft komplett über iOS Keychain-AutoFill
    /// (textContentType(.password)) — nie in UserDefaults gespeichert.
    var lastEmail: String? {
        get { defaults.string(forKey: lastEmailKey) }
        set {
            if let v = newValue, !v.isEmpty { defaults.set(v, forKey: lastEmailKey) }
            else { defaults.removeObject(forKey: lastEmailKey) }
        }
    }

    /// v1.10.63: Zustand des "Anmeldedaten merken"-Toggle. Default true —
    /// User erwartet dass App sich merkt. Bei false: kein lastEmail-Speichern.
    var rememberCredentials: Bool {
        get { defaults.object(forKey: rememberKey) as? Bool ?? true }
        set { defaults.set(newValue, forKey: rememberKey) }
    }

    func bootstrap() async {
        // v1.10.59: Wenn kein Server konfiguriert ist, den default nutzen
        // statt auf einen expliziten Setup-Screen zu warten. Marcus's
        // Wunsch: direkt auf Login-Screen landen mit Server bereits gesetzt.
        let raw = defaults.string(forKey: serverURLKey)
        let url = raw.flatMap(URL.init(string:)) ?? Self.defaultServerURL
        // Falls es der default ist und noch nicht persistiert, direkt
        // speichern — damit "Change server" ihn korrekt wieder anzeigt.
        if raw == nil {
            defaults.set(url.absoluteString, forKey: serverURLKey)
        }
        serverURL = url
        let token = Keychain.get(tokenKey)
        api = NimShareAPI(baseURL: url, token: token)
        if token != nil, let api = api {
            do {
                let me = try await api.me()
                user = me
                state = .signedIn
                return
            } catch ApiError.notAuthorized {
                // v1.10.79: NUR bei echtem 401 den Token wegwerfen. Vorher
                // haben transiente Netzwerkfehler (DNS, 5xx, Server down)
                // die Session komplett gekillt — User musste beim nächsten
                // Launch neu einloggen ohne dass es einen Auth-Grund gab.
                Keychain.remove(forKey: tokenKey)
                api.setToken(nil)
                state = .needsLogin
                return
            } catch {
                // Netzwerkfehler / Server down → wir GLAUBEN dem lokalen
                // Token und tun so als wären wir signedIn. Beim nächsten
                // API-Call kommt der echte Auth-Check.
                state = .signedIn
                return
            }
        }
        state = .needsLogin
    }

    func setServer(_ url: URL) {
        serverURL = url
        defaults.set(url.absoluteString, forKey: serverURLKey)
        api = NimShareAPI(baseURL: url)
        state = .needsLogin
    }

    /// Returned by `login` when the server demanded a TOTP code. The caller
    /// shows the 2FA challenge screen and eventually calls `completeTotpLogin`.
    @Published var pendingTotpChallenge: String?

    func login(email: String, password: String) async throws {
        guard let api else { throw ApiError.network("No server") }
        let result = try await api.login(email: email, password: password)
        switch result {
        case .success(let resp):
            Keychain.set(resp.token, forKey: tokenKey)
            api.setToken(resp.token)
            user = resp.user
            if rememberCredentials { lastEmail = email }
            state = .signedIn
        case .totpRequired(let challenge):
            pendingTotpChallenge = challenge
            // v1.10.149: lastEmail NICHT während der 2FA-Zwischenstufe schreiben —
            // LoginView.doLogin() cleanup läuft nur, wenn login() ohne 2FA
            // zurückkehrt. Bei „Anmeldedaten merken=aus" wurde die Email trotzdem
            // in UserDefaults persistiert. Erst nach erfolgreichem TOTP setzen.
        }
    }

    func completeTotpLogin(code: String) async throws {
        guard let api, let ch = pendingTotpChallenge else { throw ApiError.network("No challenge in flight") }
        let resp = try await api.loginTotp(challengeToken: ch, code: code)
        Keychain.set(resp.token, forKey: tokenKey)
        api.setToken(resp.token)
        user = resp.user
        // v1.10.149: erst hier persistieren — respektiert rememberCredentials.
        if rememberCredentials { lastEmail = resp.user.email }
        pendingTotpChallenge = nil
        state = .signedIn
    }

    func cancelTotpChallenge() {
        pendingTotpChallenge = nil
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
        // v1.10.59: NICHT auf nil setzen — wir gehen sofort auf den
        // Default-Server zurück und lassen den User via ServerConfig-Sheet
        // ändern. Damit bleibt die App IMMER in einem funktionierenden Zustand.
        serverURL = Self.defaultServerURL
        api = NimShareAPI(baseURL: Self.defaultServerURL)
        state = .needsLogin
    }
}
