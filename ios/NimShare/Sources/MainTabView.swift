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
                .tabItem { Label("Profil", systemImage: "person.crop.circle") }
        }
    }
}
