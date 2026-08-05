import Foundation
import Security

/// Minimal Keychain wrapper for the JWT bearer token.
enum Keychain {
    private static let service = "email.nimtz.nimshare"

    /// v1.11.55: gibt jetzt zurück, ob der Token wirklich persistiert wurde —
    /// vorher wurde der OSStatus von SecItemAdd verworfen, und ein Login
    /// konnte als "erfolgreich" durchgehen, obwohl der Token beim nächsten
    /// App-Start (Keychain.get liefert nil) schon wieder weg war: stiller
    /// Logout ohne jede Fehlermeldung.
    @discardableResult
    static func set(_ value: String, forKey key: String) -> Bool {
        remove(forKey: key)
        guard let data = value.data(using: .utf8) else { return false }
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key,
            kSecValueData as String: data,
            // v1.11.82 (Security-Review): WhenUnlocked statt AfterFirstUnlock — das
            // Voll-Session-Token darf nur bei entsperrtem Gerät entschlüsselbar sein,
            // nicht dauerhaft nach dem ersten Unlock seit Boot. ThisDeviceOnly bleibt
            // (kein iCloud-Sync, kein Backup-Leak). Die App liest den Token ohnehin nur
            // im Vordergrund (Login/Bootstrap/Request-Header), kein Background-Bedarf.
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
        ]
        return SecItemAdd(q as CFDictionary, nil) == errSecSuccess
    }

    static func get(_ key: String) -> String? {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data, let s = String(data: data, encoding: .utf8) else { return nil }
        return s
    }

    static func remove(forKey key: String) {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key,
        ]
        SecItemDelete(q as CFDictionary)
    }
}
