import SwiftUI

/// v1.11.59: FolderPickerSheet — echter Live-Ordner-Browser für Move/Copy
/// (Web-Parität). Ersetzt den früheren, vorab komplett aufgeklappten Baum
/// (Marcus: "unterschiedliche Darstellungen um das Ziel auszuwählen,
/// statisch, kein wirkliches Browse"). `crossScope` false (Move) startet
/// direkt im aktuellen Scope/Group — kein Bibliotheks-Screen, Cross-Scope-
/// Moves lehnt der Server eh ab. `crossScope` true (Copy) zeigt zuerst
/// "Persönlich"/"Öffentlich" zur Auswahl. Danach Breadcrumb-Drilldown durch
/// Unterordner; .searchable() filtert live über die bereits geladenen
/// Daten (kein Server-Roundtrip pro Tastendruck) und springt bei Auswahl
/// direkt zum Treffer. `excludeFolderId` blendet beim Ordner-Move/Copy den
/// Ordner selbst + alle Nachfahren aus — bislang fehlte dieser Filter auf
/// iOS komplett (der Server lehnte den Zyklus zwar mit 409 ab, aber der
/// User lief im Picker in eine Sackgasse statt dass die Option gar nicht
/// erst erscheint).
struct FolderPickerSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let title: String
    let crossScope: Bool
    let currentScope: String
    var currentGroupId: UUID? = nil
    var excludeFolderId: UUID? = nil
    let onPicked: (UUID, String) -> Void

    @State private var nodes: [NimShareAPI.WritableFolderNode] = []
    @State private var path: [NimShareAPI.WritableFolderNode] = []
    @State private var loading = true
    @State private var error: String?
    @State private var searchText = ""

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                if loading {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let e = error {
                    VStack(spacing: 10) {
                        Image(systemName: "exclamationmark.triangle").font(.title).foregroundStyle(Theme.warnRed)
                        Text(e).font(.footnote).multilineTextAlignment(.center).padding()
                    }.frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if searchText.trimmingCharacters(in: .whitespaces).isEmpty {
                    if path.isEmpty {
                        libraryPicker
                    } else {
                        breadcrumbBar
                        browseList
                    }
                } else {
                    searchResults
                }
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .searchable(text: $searchText, placement: .navigationBarDrawer(displayMode: .always), prompt: "Ordner suchen…")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Hier ablegen") {
                        if let cur = path.last { onPicked(cur.id, fullLabel(path)); dismiss() }
                    }.disabled(path.isEmpty)
                }
            }
            .task { await load() }
        }
    }

    private var libraryPicker: some View {
        List(roots()) { root in
            Button {
                path = [root]
            } label: {
                HStack {
                    Image(systemName: iconFor(root)).foregroundStyle(Theme.tungstenBlue)
                    Text(labelFor(root))
                    Spacer()
                    Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
        .listStyle(.plain)
    }

    private var breadcrumbBar: some View {
        Group {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 4) {
                    ForEach(Array(path.enumerated()), id: \.element.id) { i, n in
                        if i > 0 { Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.secondary) }
                        Button(labelFor(n)) { path = Array(path.prefix(i + 1)) }
                            .font(.footnote)
                            .foregroundStyle(i == path.count - 1 ? Color.primary : Theme.tungstenBlue)
                            .disabled(i == path.count - 1)
                    }
                }.padding(.horizontal).padding(.vertical, 8)
            }
            Divider()
        }
    }

    private var browseList: some View {
        let current = path.last!
        let kids = children(of: current.id)
        return Group {
            if kids.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "folder").font(.title).foregroundStyle(.secondary)
                    Text("Keine Unterordner.").font(.footnote).foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(kids) { n in
                    Button {
                        path.append(n)
                    } label: {
                        HStack {
                            Image(systemName: "folder.fill").foregroundStyle(Theme.tungstenBlue)
                            Text(n.name ?? "(unbenannt)")
                            Spacer()
                            Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                }
                .listStyle(.plain)
            }
        }
    }

    private var searchResults: some View {
        let q = searchText.trimmingCharacters(in: .whitespaces).lowercased()
        let hits = nodes.filter { ($0.name ?? "").lowercased().contains(q) || ($0.path ?? "").lowercased().contains(q) }
            .sorted { ($0.name ?? "") < ($1.name ?? "") }
            .prefix(40)
        return Group {
            if hits.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").font(.title).foregroundStyle(.secondary)
                    Text("Keine passenden Ordner gefunden.").font(.footnote).foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(Array(hits)) { n in
                    Button {
                        path = ancestry(of: n)
                        searchText = ""
                    } label: {
                        HStack(alignment: .top) {
                            Image(systemName: iconFor(n)).foregroundStyle(Theme.tungstenBlue)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(n.name ?? "(unbenannt)")
                                if let p = n.path, !p.isEmpty { Text(p).font(.caption2).foregroundStyle(.secondary) }
                            }
                            Spacer()
                            Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                }
                .listStyle(.plain)
            }
        }
    }

    private func roots() -> [NimShareAPI.WritableFolderNode] {
        nodes.filter { $0.isRoot == true }.sorted { rank(of: $0) < rank(of: $1) }
    }
    private func rank(of n: NimShareAPI.WritableFolderNode) -> Int {
        n.scope == "Personal" ? 0 : n.scope == "Public" ? 1 : 2
    }
    private func children(of parentId: UUID) -> [NimShareAPI.WritableFolderNode] {
        nodes.filter { $0.parentId == parentId }.sorted { ($0.name ?? "") < ($1.name ?? "") }
    }
    private func ancestry(of n: NimShareAPI.WritableFolderNode) -> [NimShareAPI.WritableFolderNode] {
        var parts = [n]
        var cur = n
        while let pid = cur.parentId, let parent = nodes.first(where: { $0.id == pid }) {
            parts.insert(parent, at: 0)
            cur = parent
        }
        return parts
    }
    private func labelFor(_ n: NimShareAPI.WritableFolderNode) -> String {
        if n.isRoot == true {
            return n.scope == "Personal" ? "Persönlich" : n.scope == "Public" ? "Öffentlich" : (n.name ?? n.path ?? "?")
        }
        return n.name ?? n.path ?? "?"
    }
    private func fullLabel(_ path: [NimShareAPI.WritableFolderNode]) -> String {
        path.map(labelFor).joined(separator: " / ")
    }
    private func iconFor(_ n: NimShareAPI.WritableFolderNode) -> String {
        guard n.isRoot == true else { return "folder.fill" }
        return n.scope == "Personal" ? "person.crop.circle" : n.scope == "Public" ? "globe" : "person.3"
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do {
            var items = try await api.writableFoldersAll()
            if let ex = excludeFolderId {
                var excludeSet: Set<UUID> = [ex]
                var grew = true
                while grew {
                    grew = false
                    for it in items where it.parentId != nil && excludeSet.contains(it.parentId!) && !excludeSet.contains(it.id) {
                        excludeSet.insert(it.id); grew = true
                    }
                }
                items = items.filter { !excludeSet.contains($0.id) }
            }
            if !crossScope {
                items = items.filter { $0.scope == currentScope && (currentScope != "Group" || $0.groupId == currentGroupId) }
            }
            nodes = items
            if !crossScope, let root = items.first(where: { $0.isRoot == true }) {
                path = [root]
            }
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
