import SwiftUI

@main
struct NimShareApp: App {
    @StateObject private var auth = AuthStore()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(auth)
                .tint(Theme.tungstenBlue)
        }
    }
}

struct RootView: View {
    @EnvironmentObject var auth: AuthStore

    var body: some View {
        Group {
            switch auth.state {
            case .booting:
                ProgressView().task { await auth.bootstrap() }
            case .needsServer:
                ServerConfigView()
            case .needsLogin:
                LoginView()
            case .signedIn:
                MainTabView()
            }
        }
    }
}
