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

    /// v2.0.5: Optionales biometrisches App-Schloss (Face ID / Touch ID).
    /// true = eine bestehende Sitzung ist gesperrt und muss vor Nutzung
    /// biometrisch entsperrt werden. Rein lokal, kein Server-Gegenpart.
    @Published var isLocked = false

    // v1.10.165: AI-Consent-Cache (Apple 5.1.1(i)). nil = noch nicht geladen,
    // false = User hat abgelehnt oder noch nie zugestimmt, true = zugestimmt.
    // Wird bei bootstrap + Login geladen und nach setAiConsent aktualisiert.
    @Published var aiConsented: Bool?
    @Published var aiProviderInfo: NimShareAPI.AiProviderInfo?

    /// Von Feature-Views vor jedem AI-Aufruf zu prüfen. true = geht schon, false = Sheet zeigen.
    var aiReady: Bool { aiConsented == true }

    /// v1.11.63: zentrale Admin-Prüfung statt des Inline-String-Vergleichs
    /// `auth.user?.role == "Admin"`, der bislang an mehreren Stellen dupliziert war.
    var isAdmin: Bool { user?.role == "Admin" }

    // v1.11.55: ProfileView's Toggle startet bei jedem Flip einen eigenen,
    // unabhängigen Task ohne Sequenzierung. Bei schnellem Doppel-Tap konnte
    // die ältere Server-Antwort NACH der neueren zurückkommen und
    // aiConsented auf den veralteten Wert zurücksetzen. Generation-Token:
    // nur die zuletzt gestartete Anfrage darf noch schreiben.
    private var aiConsentGeneration = 0

    /// Wird von AiConsentSheet aufgerufen mit true/false. Persistiert server-seitig.
    func setAiConsent(_ granted: Bool) async {
        guard let api else { return }
        aiConsentGeneration += 1
        let myGeneration = aiConsentGeneration
        do {
            let dto = try await api.setAiConsent(granted)
            guard myGeneration == aiConsentGeneration else { return }
            aiConsented = dto.consented
        } catch {
            // v1.10.171: Bei Netzwerk-Fehler den zuletzt bekannten Server-Zustand
            // NICHT überschreiben — sonst würde ein kurzer Netz-Aussetzer den
            // Consent-Flag lokal auf false flippen und den User re-nag'gen.
        }
    }

    func refreshAiConsent() async {
        guard let api else { return }
        async let consent = api.aiConsent()
        async let info = api.aiProviderInfo()
        // v1.10.171: nur überschreiben wenn wir eine echte Server-Antwort haben.
        // Bei Netz-Fehler bleibt der letzte bekannte Wert erhalten.
        if let c = try? await consent { aiConsented = c.consented }
        if let i = try? await info { aiProviderInfo = i }
    }

    /// v1.10.171: Server-403 „ai_consent_required" behandeln. AI-Views
    /// (Chat, Suche, Draft) rufen das im catch-Block, damit ein von einem
    /// zweiten Gerät widerrufener Consent hier lokal spiegelt und das
    /// Consent-Sheet automatisch aufgeht statt einer rohen 403-Fehlermeldung.
    func handleServerErrorForAiConsent(_ error: Error) -> Bool {
        // v1.11.55: die alte "http 403"-Substring-Prüfung matchte nie —
        // String(describing:) auf ApiError.http(403, ...) liefert "http(403, ...)"
        // ohne Leerzeichen. Jeder rohe 403 auf einem AI-Endpoint (nicht nur der
        // spezifische ai_consent_required-Body) soll das Consent-Sheet öffnen.
        if case .http(403, _) = error as? ApiError {
            aiConsented = false
            return true
        }
        let s = String(describing: error).lowercased()
        if s.contains("ai_consent_required") {
            aiConsented = false
            return true
        }
        return false
    }

    private let defaults = UserDefaults.standard
    private let serverURLKey = "nimshare.serverURL"
    private let tokenKey = "nimshare.jwt"
    private let lastEmailKey = "nimshare.lastEmail"
    private let rememberKey = "nimshare.rememberCredentials"
    private let biometricLockKey = "nimshare.biometricLock"

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

    /// v2.0.5: Nutzer-Opt-in für das biometrische App-Schloss. Default false.
    var biometricLockEnabled: Bool {
        get { defaults.bool(forKey: biometricLockKey) }
        set { defaults.set(newValue, forKey: biometricLockKey) }
    }

    /// Sperrt eine bestehende Sitzung, wenn das Schloss aktiviert und
    /// Biometrie verfügbar ist (App-Start / Rückkehr aus dem Hintergrund).
    func lockIfEnabled() {
        guard state == .signedIn, biometricLockEnabled, BiometricAuth.available != .none else { return }
        isLocked = true
    }

    /// Führt die Biometrie aus; bei Erfolg wird entsperrt. true = entsperrt.
    func unlock() async -> Bool {
        let ok = await BiometricAuth.authenticate(reason: String(localized: "NimShare entsperren"))
        if ok { isLocked = false }
        return ok
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
                lockIfEnabled()
                Task { await refreshAiConsent() }
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
                lockIfEnabled()
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
            // v1.11.55: nicht mehr blind auf "signedIn" gehen, wenn der Token
            // gar nicht im Keychain landet — sonst wirkt der Login erfolgreich,
            // ist aber beim nächsten App-Start spurlos wieder ausgeloggt.
            guard Keychain.set(resp.token, forKey: tokenKey) else {
                throw ApiError.network("Could not save your session securely. Please try again.")
            }
            api.setToken(resp.token)
            user = resp.user
            if rememberCredentials { lastEmail = email }
            state = .signedIn
            Task { await refreshAiConsent() }
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
        guard Keychain.set(resp.token, forKey: tokenKey) else {
            throw ApiError.network("Could not save your session securely. Please try again.")
        }
        api.setToken(resp.token)
        user = resp.user
        // v1.10.149: erst hier persistieren — respektiert rememberCredentials.
        if rememberCredentials { lastEmail = resp.user.email }
        pendingTotpChallenge = nil
        state = .signedIn
        Task { await refreshAiConsent() }
    }

    func cancelTotpChallenge() {
        pendingTotpChallenge = nil
    }

    func signOut() {
        Keychain.remove(forKey: tokenKey)
        api?.setToken(nil)
        user = nil
        isLocked = false
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
