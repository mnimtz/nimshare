import SwiftUI

struct BrowseRootView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var scopes: [ScopeTile] = []
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        Group {
            if loading {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error {
                errorView(e)
            } else if scopes.isEmpty {
                ContentUnavailableView("No libraries", systemImage: "folder", description: Text("No scopes returned from the server."))
            } else {
                content
            }
        }
        .navigationTitle("Files")
        .task { await load() }
        .refreshable { await load() }
    }

    private var content: some View {
        List {
            Section("My libraries") {
                ForEach(scopes.filter { $0.scope.lowercased() == "personal" }) { tile in
                    scopeRow(tile)
                }
            }
            Section("Shared") {
                ForEach(scopes.filter { $0.scope.lowercased() == "public" }) { tile in
                    scopeRow(tile)
                }
            }
            let groups = scopes.filter { $0.scope.lowercased() == "group" }
            if !groups.isEmpty {
                Section("Group libraries") {
                    ForEach(groups) { tile in
                        scopeRow(tile)
                    }
                }
            }
        }
    }

    private func scopeRow(_ tile: ScopeTile) -> some View {
        NavigationLink {
            FolderBrowserView(scope: tile.scope, groupId: tile.groupId, path: "", title: tile.name)
        } label: {
            HStack(spacing: 14) {
                Image(systemName: tile.systemImage)
                    .font(.title2)
                    .foregroundStyle(Theme.tungstenBlue)
                    .frame(width: 32)
                VStack(alignment: .leading, spacing: 2) {
                    Text(tile.name).font(.body.weight(.medium))
                    Text(tile.scope.capitalized).font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(.vertical, 4)
        }
    }

    private func errorView(_ e: String) -> some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
            Text(e).multilineTextAlignment(.center).padding(.horizontal)
            Button("Retry") { Task { await load() } }
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { scopes = try await api.scopes() }
        catch let e as ApiError {
            error = e.localizedDescription
            if case .notAuthorized = e { auth.signOut() }
        }
        catch let ex { error = ex.localizedDescription }
    }
}
