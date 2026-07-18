import SwiftUI

struct FolderBrowserView: View {
    @EnvironmentObject var auth: AuthStore
    let scope: String
    let groupId: UUID?
    let path: String
    let title: String

    @State private var data: BrowseResponse?
    @State private var loading = true
    @State private var error: String?
    @State private var previewFile: FileItem?

    var body: some View {
        Group {
            if loading && data == nil {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, data == nil {
                errorView(e)
            } else if let d = data {
                list(d)
            }
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .task(id: path) { await load() }
        .refreshable { await load() }
        .sheet(item: $previewFile) { file in
            NavigationStack { FilePreviewView(file: file) }
        }
    }

    private func list(_ d: BrowseResponse) -> some View {
        List {
            if !d.subfolders.isEmpty {
                Section("Folders") {
                    ForEach(d.subfolders) { f in
                        NavigationLink {
                            FolderBrowserView(
                                scope: scope, groupId: groupId,
                                path: joinPath(path, f.name),
                                title: f.name
                            )
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: "folder.fill")
                                    .foregroundStyle(Theme.tungstenBlue)
                                    .frame(width: 24)
                                Text(f.name)
                            }
                        }
                    }
                }
            }
            if !d.files.isEmpty {
                Section("Files") {
                    ForEach(d.files) { f in
                        Button { previewFile = f } label: {
                            FileRowView(file: f)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            if d.subfolders.isEmpty && d.files.isEmpty {
                ContentUnavailableView("Empty", systemImage: "tray", description: Text("This folder is empty."))
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
            }
        }
    }

    private func joinPath(_ base: String, _ segment: String) -> String {
        let escaped = segment.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? segment
        return base.isEmpty ? escaped : base + "/" + escaped
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
        do {
            data = try await api.browse(scope: scope, groupId: groupId, path: path.isEmpty ? nil : path)
        } catch let e as ApiError {
            error = e.localizedDescription
            if case .notAuthorized = e { auth.signOut() }
        } catch let ex { error = ex.localizedDescription }
    }
}

struct FileRowView: View {
    let file: FileItem

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: file.iconName)
                .foregroundStyle(Theme.tungstenBlue)
                .frame(width: 24, alignment: .center)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 4) {
                Text(file.name).lineLimit(2)
                HStack(spacing: 8) {
                    Text(byteCountFormatter.string(fromByteCount: file.sizeBytes))
                        .font(.caption).foregroundStyle(.secondary)
                    if let owner = file.ownerName {
                        Text("· " + owner).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                if !file.tags.isEmpty || file.aiRiskFlag != nil {
                    HStack(spacing: 6) {
                        if let risk = file.aiRiskFlag {
                            Chip(text: "⚠ " + risk, color: Theme.warnRed, bg: Theme.warnRed.opacity(0.12))
                        }
                        ForEach(file.tags.prefix(3), id: \.self) { tag in
                            Chip(text: tag, color: Theme.tungstenBlue, bg: Theme.aiBlueTintBg)
                        }
                    }
                }
            }
        }
        .padding(.vertical, 2)
    }
}

struct Chip: View {
    let text: String
    let color: Color
    let bg: Color
    var body: some View {
        Text(text)
            .font(.caption2.weight(.medium))
            .padding(.horizontal, 6).padding(.vertical, 2)
            .foregroundStyle(color)
            .background(bg)
            .clipShape(RoundedRectangle(cornerRadius: 4))
    }
}

private let byteCountFormatter: ByteCountFormatter = {
    let f = ByteCountFormatter()
    f.countStyle = .file
    return f
}()
