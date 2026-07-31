import SwiftUI

struct MainTabView: View {
    var body: some View {
        TabView {
            NavigationStack { BrowseRootView() }
                .tabItem { Label("Dateien", systemImage: "folder.fill") }
            NavigationStack { SearchView() }
                .tabItem { Label("Suche", systemImage: "sparkle.magnifyingglass") }
            NavigationStack { ChatView() }
                .tabItem { Label("Chat", systemImage: "message.badge.filled.fill") }
            NavigationStack { NotificationsView() }
                .tabItem { Label("Meldungen", systemImage: "bell.fill") }
            NavigationStack { ProfileView() }
                // v1.11.63: "Profil" umbenannt — die Seite enthält längst mehr
                // als Profil-Infos (Sicherheit, Server, Rechtliches, jetzt auch
                // Aktivität), Marcus's Feedback: der Name ist missverständlich.
                .tabItem { Label("Einstellungen", systemImage: "gearshape.fill") }
        }
    }
}
