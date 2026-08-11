import SwiftUI

@main
struct NimShareApp: App {
    @StateObject private var auth = AuthStore()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(auth)
                // v1.11.73 — Redesign-Pilot: globaler Tint auf Theme.cyan
                // (statt tungstenBlue) — matcht die Interaktions-Akzentfarbe,
                // die alle redesignten Screens für Buttons/Links/Toggles
                // benutzen (Theme.navy bleibt Icon-/Marken-Akzent).
                .tint(Theme.cyan)
        }
    }
}

/// v1.11.73 — Erscheinungsbild-Picker (Marcus's Wunsch, Teil der
/// Einstellungen-Neugestaltung). "system" folgt dem Geräte-Setting (kein
/// Override), "light"/"dark" erzwingen ein Schema app-weit.
enum AppearanceMode: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: Self { self }
    var label: LocalizedStringKey {
        switch self {
        case .system: return "System"
        case .light: return "Hell"
        case .dark: return "Dunkel"
        }
    }
    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

struct RootView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage("appearance.mode") private var appearanceRaw: String = AppearanceMode.system.rawValue
    // v2.0.7 (Audit): Sichtschutz solange die Szene nicht aktiv ist — deckt den
    // App-Switcher-Snapshot ab, OHNE das Schloss zu armen (die Face-ID-System-UI
    // löst selbst .inactive aus, darum bleibt das Re-Lock weiter an .background).
    @State private var privacyShield = false

    var body: some View {
        Group {
            switch auth.state {
            case .booting:
                ProgressView().task {
                    // v2.0.7 (Audit): alte Temp-Downloads beim Start abräumen
                    // (>24 h) — bisher gab es den in PrivacyInfo deklarierten
                    // Cleanup gar nicht; Dateien lagen bis zum iOS-Purge herum.
                    TmpFile.cleanupSweep()
                    await auth.bootstrap()
                }
            case .needsServer:
                ServerConfigView()
            case .needsLogin:
                if auth.pendingTotpChallenge != nil {
                    TotpChallengeView()
                } else {
                    LoginView()
                }
            case .signedIn:
                // v2.0.5: biometrisches App-Schloss (Variante A) vor der
                // eigentlichen Oberfläche. isLocked wird beim Bootstrap und
                // beim Wechsel in den Hintergrund gesetzt (siehe AuthStore).
                if auth.isLocked {
                    LockScreenView()
                } else {
                    MainTabView()
                }
            }
        }
        .overlay {
            if privacyShield && auth.state == .signedIn && !auth.isLocked {
                PrivacyShieldView()
            }
        }
        .preferredColorScheme((AppearanceMode(rawValue: appearanceRaw) ?? .system).colorScheme)
        // Beim Wechsel in den Hintergrund sperren, damit beim nächsten Öffnen
        // wieder Biometrie verlangt wird. Nur .background (nicht .inactive) —
        // sonst würde die Face-ID-System-UI selbst ein Re-Lock auslösen.
        .onChange(of: scenePhase) { _, phase in
            withAnimation(.easeInOut(duration: 0.15)) { privacyShield = phase != .active }
            if phase == .background { auth.lockIfEnabled() }
        }
    }
}

/// v2.0.7 (Audit): Blur-Overlay für den App-Switcher — Dateinamen/Chat-Inhalte
/// sind sonst im Snapshot lesbar, selbst wenn das Face-ID-Schloss aktiv ist.
/// Bewusst ohne Text (keine Lokalisierung nötig), nur Marke auf Material.
struct PrivacyShieldView: View {
    var body: some View {
        ZStack {
            Rectangle().fill(.regularMaterial)
            Image(systemName: "lock.shield.fill")
                .font(.system(size: 56))
                .foregroundStyle(Theme.navy.opacity(0.55))
        }
        .ignoresSafeArea()
        .transition(.opacity)
    }
}
