import Foundation
import LocalAuthentication

/// v2.0.5: Dünner Wrapper um LocalAuthentication für das optionale
/// biometrische App-Schloss (Variante A). Rein lokale Geräte-Auth, die die
/// bereits authentifizierte Sitzung absichert — kein Server-Gegenpart
/// (bewusst client-only, siehe Sicherheits-Review v2.0.4).
enum BiometricAuth {
    enum Kind {
        case faceID, touchID, opticID, none

        /// Marken-Eigennamen — werden NICHT übersetzt (EFIGS+NL n/a).
        var brand: String {
            switch self {
            case .faceID: return "Face ID"
            case .touchID: return "Touch ID"
            case .opticID: return "Optic ID"
            case .none: return ""
            }
        }

        var systemImage: String {
            switch self {
            case .faceID: return "faceid"
            case .touchID: return "touchid"
            case .opticID: return "opticid"
            case .none: return "lock"
            }
        }
    }

    /// Welche Biometrie ist auf diesem Gerät eingerichtet und nutzbar?
    static var available: Kind {
        let ctx = LAContext()
        var err: NSError?
        guard ctx.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &err) else {
            return .none
        }
        switch ctx.biometryType {
        case .faceID: return .faceID
        case .touchID: return .touchID
        case .opticID: return .opticID
        default: return .none
        }
    }

    /// Führt die Biometrie aus, mit Fallback auf den Geräte-Code — damit ein
    /// Face-ID-Fehlschlag den Nutzer nicht dauerhaft aussperrt. true = ok.
    static func authenticate(reason: String) async -> Bool {
        let ctx = LAContext()
        var err: NSError?
        guard ctx.canEvaluatePolicy(.deviceOwnerAuthentication, error: &err) else { return false }
        do {
            return try await ctx.evaluatePolicy(.deviceOwnerAuthentication, localizedReason: reason)
        } catch {
            return false
        }
    }
}
