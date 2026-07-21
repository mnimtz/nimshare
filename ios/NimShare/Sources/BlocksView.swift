import SwiftUI

/// v1.10.82: „Blockierte Nutzer" — App-Store-Blocker Apple 1.2. User
/// müssen ihre Blockliste einsehen und Blocks aufheben können.
struct BlocksView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var rows: [NimShareAPI.BlockedUserDto] = []
    @State private var loading = false
    @State private var error: String?

    var body: some View {
        List {
            if rows.isEmpty && !loading {
                Section {
                    Text("Du hast noch niemanden blockiert.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
            ForEach(rows) { r in
                VStack(alignment: .leading, spacing: 2) {
                    Text(r.blockedName ?? r.blockedEmail ?? r.blockedUserId.uuidString)
                        .font(.body)
                    if let email = r.blockedEmail, r.blockedName != nil {
                        Text(email).font(.caption).foregroundStyle(.secondary)
                    }
                    if let reason = r.reason, !reason.isEmpty {
                        // v1.10.91: extended delimiters, sonst zerlegt das
                        // typografische „…" den Swift-String und der Parser
                        // wirft „unterminated string literal".
                        Text(#"„\#(reason)""#).font(.caption).foregroundStyle(.secondary)
                    }
                    Text(r.createdAt, style: .date).font(.caption2).foregroundStyle(.secondary)
                }
                .swipeActions {
                    Button(role: .destructive) {
                        Task { await unblock(r.blockedUserId) }
                    } label: {
                        Label("Aufheben", systemImage: "person.badge.plus")
                    }
                }
            }
            if let e = error {
                Section { Text(e).foregroundStyle(Theme.warnRed).font(.footnote) }
            }
        }
        .navigationTitle("Blockierte Nutzer")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await load() }
        .task { await load() }
        .overlay { if loading && rows.isEmpty { ProgressView() } }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { rows = try await api.listBlocks() }
        catch let ex { error = ex.localizedDescription }
    }

    private func unblock(_ id: UUID) async {
        guard let api = auth.api else { return }
        do {
            try await api.unblockUser(id)
            await load()
        } catch let ex { error = ex.localizedDescription }
    }
}
