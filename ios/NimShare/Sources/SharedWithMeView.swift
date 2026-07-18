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
                ContentUnavailableView("Nichts freigegeben", systemImage: "person.crop.circle",
                    description: Text("Hier landen Dateien und Ordner, die andere für dich freigeben."))
            } else {
                List(items) { item in
                    Button {
                        if item.kind == "file" {
                            previewFile = FileItem(
                                id: item.id, name: item.name, sizeBytes: 0,
                                contentType: "application/octet-stream",
                                createdAt: item.sharedAt, ownerName: item.sharedByName,
                                aiTags: nil, aiRiskFlag: nil)
                        }
                    } label: {
                        HStack {
                            Image(systemName: item.kind == "file" ? "doc.fill" : "folder.fill")
                                .foregroundStyle(item.kind == "file" ? Color.blue : Color.orange)
                                .frame(width: 24)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.name).lineLimit(2)
                                HStack(spacing: 6) {
                                    Text("von \(item.sharedByName)").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                    permBadge(item.permissionEnum)
                                }
                            }
                            Spacer()
                            Image(systemName: "chevron.right").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .buttonStyle(.plain)
                }
            }
            if let e = error { Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding() }
        }
        .navigationTitle("Für mich freigegeben")
        .task { await load() }
        .refreshable { await load() }
        .sheet(item: $previewFile) { f in NavigationStack { FilePreviewView(file: f) } }
    }

    private func permBadge(_ perm: DirectSharePermission) -> some View {
        Text(perm == .write ? "R/W" : "R")
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(perm == .write ? Color.orange.opacity(0.2) : Color.blue.opacity(0.15))
            .foregroundStyle(perm == .write ? .orange : Theme.tungstenBlue)
            .clipShape(RoundedRectangle(cornerRadius: 3))
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.sharedWithMe() }
        catch let ex { error = ex.localizedDescription }
    }
}
