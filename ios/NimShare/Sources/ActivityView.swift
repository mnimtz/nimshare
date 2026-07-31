import SwiftUI

struct ActivityView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var items: [ActivityDto] = []
    @State private var loading = true
    @State private var showAll = false
    @State private var error: String?

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                RSEmptyState(systemImage: "clock", title: String(localized: "Noch keine Aktivität"),
                             desc: String(localized: "Aktionen wie Uploads, Freigaben und Löschungen erscheinen hier."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    if auth.user?.role == "Admin" {
                        Picker(String(localized: "Sichtbarkeit"), selection: $showAll) {
                            Text("Meine").tag(false)
                            Text("Alle Nutzer").tag(true)
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .onChange(of: showAll) { _, _ in Task { await load() } }
                        .listRowBackground(Color.clear)
                        .listRowSeparator(.hidden)
                    }
                    ForEach(items) { item in
                        HStack(alignment: .top, spacing: 12) {
                            ZStack {
                                Circle().fill(Theme.navy.opacity(0.12)).frame(width: 34, height: 34)
                                Image(systemName: item.iconName)
                                    .font(.system(size: 14, weight: .semibold))
                                    .foregroundStyle(Theme.navy)
                            }
                            VStack(alignment: .leading, spacing: 3) {
                                Text(item.summary).font(TFont.bodyM).foregroundStyle(Theme.textPrimary).lineLimit(3)
                                HStack(spacing: 6) {
                                    Text(item.actorName).font(TFont.caption).foregroundStyle(Theme.textSecondary)
                                    Text("·").font(TFont.caption).foregroundStyle(Theme.textSecondary)
                                    Text(item.at.formatted(.relative(presentation: .named)))
                                        .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                                }
                            }
                        }
                        .padding(.vertical, 4)
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                    }
                }
                .scrollContentBackground(.hidden)
            }
            if let e = error { Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding() }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .navigationTitle(String(localized: "Aktivität"))
        .task { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.activity(all: showAll) }
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}
