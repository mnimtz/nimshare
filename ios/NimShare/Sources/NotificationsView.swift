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
                RSEmptyState(systemImage: "bell.slash", title: "Keine Benachrichtigungen",
                    desc: "Sobald etwas passiert, taucht es hier auf.")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(items) { n in
                        HStack(alignment: .top, spacing: 12) {
                            ZStack {
                                Circle().fill(Theme.navy.opacity(0.12)).frame(width: 34, height: 34)
                                Image(systemName: n.iconName)
                                    .font(.system(size: 14, weight: .semibold))
                                    .foregroundStyle(n.isUnread ? Theme.navyFg : Theme.textTertiary)
                            }
                            VStack(alignment: .leading, spacing: 3) {
                                Text(n.title)
                                    .font(n.isUnread ? TFont.titleS : TFont.bodyM)
                                    .foregroundStyle(Theme.textPrimary)
                                    .lineLimit(3)
                                if let b = n.body {
                                    Text(b).font(TFont.caption).foregroundStyle(Theme.textSecondary).lineLimit(3)
                                }
                                Text(n.createdAt.formatted(.relative(presentation: .named)))
                                    .font(TFont.caption).foregroundStyle(Theme.textTertiary)
                            }
                            Spacer()
                            if n.isUnread {
                                Circle().fill(Theme.cyan).frame(width: 8, height: 8)
                            }
                        }
                        .padding(.vertical, 4)
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                        .swipeActions {
                            Button {
                                Task { await markRead(n.id) }
                            } label: {
                                Label("Gelesen", systemImage: "checkmark")
                            }.tint(Theme.cyan)
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            }
            if let e = error { Text(e).font(TFont.bodyS).foregroundStyle(Theme.danger2).padding() }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
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
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }

    private func markRead(_ id: UUID) async {
        guard let api = auth.api else { return }
        do { try await api.markNotificationRead(id); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }

    private func markAllRead() async {
        guard let api = auth.api else { return }
        do { try await api.markAllNotificationsRead(); await load() }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}
