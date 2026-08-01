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
    @AppStorage("appearance.mode") private var appearanceRaw: String = AppearanceMode.system.rawValue

    var body: some View {
        Group {
            switch auth.state {
            case .booting:
                ProgressView().task { await auth.bootstrap() }
            case .needsServer:
                ServerConfigView()
            case .needsLogin:
                if auth.pendingTotpChallenge != nil {
                    TotpChallengeView()
                } else {
                    LoginView()
                }
            case .signedIn:
                MainTabView()
            }
        }
        .preferredColorScheme((AppearanceMode(rawValue: appearanceRaw) ?? .system).colorScheme)
    }
}
