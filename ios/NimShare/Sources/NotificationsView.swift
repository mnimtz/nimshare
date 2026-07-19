import SwiftUI

struct NotificationsView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [NotifyDto] = []
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView("Keine Benachrichtigungen", systemImage: "bell.slash",
                    description: Text("Sobald etwas passiert, taucht es hier auf."))
            } else {
                List {
                    ForEach(items) { n in
                        HStack(alignment: .top, spacing: 12) {
                            Image(systemName: n.iconName)
                                .foregroundStyle(n.isUnread ? Theme.tungstenBlue : Color.secondary)
                                .frame(width: 24, alignment: .center)
                                .padding(.top, 2)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(n.title)
                                    .font(.body.weight(n.isUnread ? .semibold : .regular))
                                    .lineLimit(3)
                                if let b = n.body {
                                    Text(b).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                                }
                                Text(n.createdAt.formatted(.relative(presentation: .named)))
                                    .font(.caption2).foregroundStyle(.secondary)
                            }
                            Spacer()
                            if n.isUnread {
                                Circle().fill(Theme.tungstenBlue).frame(width: 8, height: 8)
                            }
                        }
                        .padding(.vertical, 2)
                        .swipeActions {
                            Button {
                                Task { await markRead(n.id) }
                            } label: {
                                Label("Gelesen", systemImage: "checkmark")
                            }.tint(Theme.tungstenBlue)
                        }
                    }
                }
            }
            if let e = error { Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding() }
        }
        .navigationTitle("Benachrichtigungen")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                if items.contains(where: { $0.isUnread }) {
                    Button("Alle gelesen") { Task { await markAllRead() } }
                }
            }
        }
        .task { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.listNotifications() }
        catch let ex { error = ex.localizedDescription }
    }

    private func markRead(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.markNotificationRead(id); await load() }
        catch let ex { error = ex.localizedDescription }
    }

    private func markAllRead() async {
        guard let api = auth.api else { return }
        do { try await api.markAllNotificationsRead(); await load() }
        catch let ex { error = ex.localizedDescription }
    }
}
