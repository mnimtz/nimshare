import SwiftUI

struct SharedWithMeView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [SharedWithMeItemDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var previewFile: FileItem?

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                RSEmptyState(systemImage: "person.crop.circle", title: String(localized: "Nichts freigegeben"),
                    desc: "Hier landen Dateien und Ordner, die andere für dich freigeben.")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(items) { item in
                    // v1.10.148: Bug #7 — Ordner-Tap navigierte vorher nirgends,
                    // Row wirkte eingefroren. Jetzt: für „folder" NavigationLink
                    // in SharedFolderView, für „file" bleibt der Preview-Button.
                    Group {
                        if item.kind == "folder" {
                            NavigationLink {
                                SharedFolderView(folderId: item.id, initialTitle: item.name)
                            } label: { rowLabel(item) }
                        } else {
                            Button {
                                previewFile = FileItem(
                                    id: item.id, name: item.name, sizeBytes: 0,
                                    contentType: "application/octet-stream",
                                    createdAt: item.sharedAt, ownerName: item.sharedByName,
                                    aiTags: nil, aiRiskFlag: nil)
                            } label: { rowLabel(item) }
                            .buttonStyle(.plain)
                        }
                    }
                    .listRowBackground(Theme.surface2)
                    .listRowSeparator(.hidden)
                }
                .scrollContentBackground(.hidden)
            }
            if let e = error { Text(e).font(TFont.bodyS).foregroundStyle(Theme.danger2).padding() }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle(String(localized: "Für mich freigegeben"))
        .task { await load() }
        .refreshable { await load() }
        .sheet(item: $previewFile) { f in FileDetailView(file: f) }
    }

    @ViewBuilder
    private func rowLabel(_ item: SharedWithMeItemDto) -> some View {
        // v1.10.151: Kein manueller Chevron mehr. Für Ordner-Rows zeichnet
        // NavigationLink automatisch einen Disclosure-Indicator (vorher zwei
        // Chevrons sichtbar). Datei-Rows öffnen ein Sheet — dort ist ein
        // Chevron irreführend (verspricht Navigation, liefert Modal), also
        // ebenfalls weglassen.
        HStack(spacing: 12) {
            ZStack {
                Circle().fill(Theme.navy.opacity(0.12)).frame(width: 34, height: 34)
                Image(systemName: item.kind == "file" ? "doc.fill" : "folder.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(Theme.navyFg)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(item.name).font(TFont.titleS).foregroundStyle(Theme.textPrimary).lineLimit(2)
                HStack(spacing: 6) {
                    Text("von \(item.sharedByName)", comment: "shared-with-me subtitle: 'from <name>'")
                        .font(TFont.caption).foregroundStyle(Theme.textSecondary).lineLimit(1)
                    permBadge(item.permissionEnum)
                }
            }
            Spacer()
        }
    }

    private func permBadge(_ perm: DirectSharePermission) -> some View {
        Chip(text: perm == .write ? "R/W" : "R",
             color: perm == .write ? Theme.yellow : Theme.cyan,
             bg: (perm == .write ? Theme.yellow : Theme.cyan).opacity(0.12))
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.sharedWithMe() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}
