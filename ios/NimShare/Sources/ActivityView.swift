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
                ContentUnavailableView(String(localized: "Noch keine Aktivität"), systemImage: "clock",
                    description: Text("Aktionen wie Uploads, Freigaben und Löschungen erscheinen hier."))
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
                    }
                    ForEach(items) { item in
                        HStack(alignment: .top, spacing: 12) {
                            Image(systemName: item.iconName)
                                .foregroundStyle(Theme.tungstenBlue)
                                .frame(width: 24, alignment: .center)
                                .padding(.top, 2)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(item.summary).lineLimit(3)
                                HStack(spacing: 6) {
                                    Text(item.actorName).font(.caption).foregroundStyle(.secondary)
                                    Text("·").font(.caption).foregroundStyle(.secondary)
                                    Text(item.at.formatted(.relative(presentation: .named)))
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                        .padding(.vertical, 2)
                    }
                }
            }
            if let e = error { Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding() }
        }
        .navigationTitle(String(localized: "Aktivität"))
        .task { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { items = try await api.activity(all: showAll) }
        catch let ex { error = ex.localizedDescription }
    }
}
