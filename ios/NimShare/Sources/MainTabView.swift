import SwiftUI

struct MainTabView: View {
    var body: some View {
        TabView {
            NavigationStack { BrowseRootView() }
                .tabItem { Label("Files", systemImage: "folder.fill") }
            NavigationStack { SearchView() }
                .tabItem { Label("Search", systemImage: "sparkle.magnifyingglass") }
            NavigationStack { ChatView() }
                .tabItem { Label("Chat", systemImage: "message.badge.filled.fill") }
            NavigationStack { LinksView() }
                .tabItem { Label("Links", systemImage: "link") }
            NavigationStack { ProfileView() }
                .tabItem { Label("Profile", systemImage: "person.crop.circle") }
        }
    }
}
