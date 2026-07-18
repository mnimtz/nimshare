import SwiftUI

struct LinksView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var links: [ShareLinkDto] = []
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        Group {
            if loading && links.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, links.isEmpty {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                    Button("Retry") { Task { await load() } }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if links.isEmpty {
                ContentUnavailableView("No share links yet", systemImage: "link",
                                       description: Text("Create share links from the NimShare web app; they'll appear here."))
            } else {
                List(links) { link in
                    row(link)
                }
            }
        }
        .navigationTitle("My links")
        .task { await load() }
        .refreshable { await load() }
    }

    private func row(_ link: ShareLinkDto) -> some View {
        let full = URL(string: link.url)
        return VStack(alignment: .leading, spacing: 6) {
            HStack {
                Image(systemName: "link").foregroundStyle(Theme.tungstenBlue)
                Text(link.slug).font(.body.weight(.medium)).lineLimit(1)
                Spacer()
                if link.hasPassword { Image(systemName: "lock.fill").foregroundStyle(.secondary) }
                if link.isRevoked { Image(systemName: "xmark.circle.fill").foregroundStyle(Theme.warnRed) }
            }
            Text(link.url).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
            HStack(spacing: 12) {
                Text("\(link.downloadCount) downloads").font(.caption).foregroundStyle(.secondary)
                if let limit = link.maxDownloads { Text("Limit: \(limit)").font(.caption).foregroundStyle(.secondary) }
                if let exp = link.expiresAt { Text("Expires: \(exp.formatted(date: .abbreviated, time: .omitted))").font(.caption).foregroundStyle(.secondary) }
            }
            HStack(spacing: 8) {
                Button {
                    UIPasteboard.general.string = link.url
                } label: {
                    Label("Copy", systemImage: "doc.on.doc")
                }.buttonStyle(.bordered).controlSize(.small)
                if let full {
                    ShareLink(item: full) {
                        Label("Share", systemImage: "square.and.arrow.up")
                    }.buttonStyle(.bordered).controlSize(.small)
                }
            }
            .padding(.top, 2)
        }
        .padding(.vertical, 4)
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { links = try await api.listMyLinks() }
        catch let e as ApiError {
            error = e.localizedDescription
            if case .notAuthorized = e { auth.signOut() }
        } catch let ex { error = ex.localizedDescription }
    }
}
