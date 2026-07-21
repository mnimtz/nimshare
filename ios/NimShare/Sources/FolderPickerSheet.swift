import SwiftUI

/// v1.10.70: Ordner-Picker als Bottom-Sheet — für Move/Copy von Files
/// analog zum Web-Tree-Browser. Lädt writable-all einmalig, baut den
/// Baum client-side, User klickt Zielordner, bestätigt mit "Hier ablegen".
struct FolderPickerSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let title: String
    let onPicked: (UUID, String) -> Void

    @State private var nodes: [NimShareAPI.WritableFolderNode] = []
    @State private var expanded: Set<UUID> = []
    @State private var selected: UUID?
    @State private var selectedPath: String = ""
    @State private var loading = true
    @State private var error: String?

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
                } else {
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(roots(), id: \.id) { root in
                                nodeRow(root, depth: 0)
                            }
                        }
                        .padding(.vertical, 8)
                    }
                }
                Divider()
                HStack {
                    Text(selectedPath.isEmpty ? "—" : selectedPath)
                        .font(.footnote)
                        .foregroundStyle(selectedPath.isEmpty ? .secondary : Theme.tungstenBlue)
                        .lineLimit(1)
                    Spacer()
                }.padding(.horizontal).padding(.top, 8)
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Hier ablegen") {
                        if let id = selected { onPicked(id, selectedPath); dismiss() }
                    }.disabled(selected == nil)
                }
            }
            .task { await load() }
        }
    }

    private func roots() -> [NimShareAPI.WritableFolderNode] {
        let ids = Set(nodes.map { $0.id })
        let scopeRank: [String: Int] = ["Personal": 0, "Public": 1, "Group": 2]
        return nodes.filter { $0.parentId == nil || !ids.contains($0.parentId!) }
            .sorted { (scopeRank[$0.scope] ?? 9, $0.name ?? "") < (scopeRank[$1.scope] ?? 9, $1.name ?? "") }
    }

    private func children(of parentId: UUID) -> [NimShareAPI.WritableFolderNode] {
        nodes.filter { $0.parentId == parentId }.sorted { ($0.name ?? "") < ($1.name ?? "") }
    }

    @ViewBuilder
    private func nodeRow(_ n: NimShareAPI.WritableFolderNode, depth: Int) -> some View {
        let kids = children(of: n.id)
        let hasKids = !kids.isEmpty
        let isExpanded = expanded.contains(n.id)
        HStack(spacing: 6) {
            if hasKids {
                Button {
                    if isExpanded { expanded.remove(n.id) } else { expanded.insert(n.id) }
                } label: {
                    Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .frame(width: 14)
                }.buttonStyle(.plain)
            } else {
                Text("").frame(width: 14)
            }
            Image(systemName: n.isRoot == true
                  ? (n.scope == "Personal" ? "person.crop.circle"
                     : n.scope == "Public" ? "globe" : "person.3")
                  : "folder.fill")
                .foregroundStyle(Theme.tungstenBlue)
            Text(n.name ?? n.path ?? "(unbenannt)")
            Spacer()
        }
        .padding(.vertical, 6)
        .padding(.leading, CGFloat(6 + depth * 16))
        .padding(.trailing, 12)
        .background(selected == n.id ? Theme.tungstenBlue.opacity(0.18) : Color.clear)
        .contentShape(Rectangle())
        .onTapGesture {
            selected = n.id
            selectedPath = fullPath(n)
        }
        if hasKids && isExpanded {
            ForEach(kids, id: \.id) { child in
                nodeRow(child, depth: depth + 1)
            }
        }
    }

    private func fullPath(_ n: NimShareAPI.WritableFolderNode) -> String {
        var parts: [String] = [n.name ?? n.path ?? "?"]
        var cur: NimShareAPI.WritableFolderNode? = n
        while let c = cur, let pid = c.parentId, let parent = nodes.first(where: { $0.id == pid }) {
            parts.insert(parent.name ?? parent.path ?? "?", at: 0)
            cur = parent
        }
        return parts.joined(separator: " / ")
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do {
            nodes = try await api.writableFoldersAll()
            // Roots default aufgeklappt.
            let ids = Set(nodes.map { $0.id })
            for n in nodes where n.parentId == nil || !ids.contains(n.parentId!) {
                expanded.insert(n.id)
            }
        } catch let ex { error = ex.localizedDescription }
    }
}
