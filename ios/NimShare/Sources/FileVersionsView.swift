import SwiftUI

/// v1.10.72: Versionshistorie einer Datei. Liste nach VersionNumber
/// absteigend, aktuelle Version mit Chip markiert, Wiederherstellen
/// per Swipe für Nicht-Current-Versions.
struct FileVersionsView: View {
    @EnvironmentObject var auth: AuthStore
    let fileId: UUID
    let fileName: String

    @State private var items: [NimShareAPI.FileVersionDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var confirmRestore: NimShareAPI.FileVersionDto?

    var body: some View {
        Group {
            if loading && items.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                ContentUnavailableView("Keine Versionen",
                    systemImage: "clock.arrow.circlepath",
                    description: Text("Diese Datei hat nur eine Version. Beim erneuten Upload wird die alte hier archiviert."))
            } else {
                List {
                    ForEach(items) { v in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text("v\(v.versionNumber)").font(.body.weight(.semibold))
                                if v.isCurrent {
                                    Text("Aktuell")
                                        .font(.caption2.weight(.medium))
                                        .padding(.horizontal, 6).padding(.vertical, 2)
                                        .background(Color.green.opacity(0.15))
                                        .foregroundStyle(.green)
                                        .clipShape(RoundedRectangle(cornerRadius: 3))
                                }
                                Spacer()
                                Text(ByteCountFormatter.string(fromByteCount: v.sizeBytes, countStyle: .file))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Text(v.createdAt.formatted(date: .abbreviated, time: .shortened))
                                .font(.caption).foregroundStyle(.secondary)
                            Text("Hochgeladen von: \(v.createdByName)").font(.caption2).foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 2)
                        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                            if !v.isCurrent {
                                Button {
                                    confirmRestore = v
                                } label: { Label("Wiederherstellen", systemImage: "arrow.uturn.backward") }
                                    .tint(Theme.tungstenBlue)
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle("Versionen")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .refreshable { await load() }
        .confirmationDialog(
            "Diese Version wiederherstellen?",
            isPresented: Binding(get: { confirmRestore != nil }, set: { if !$0 { confirmRestore = nil } }),
            titleVisibility: .visible
        ) {
            if let v = confirmRestore {
                Button("v\(v.versionNumber) wiederherstellen") { Task { await restore(v.id) } }
                Button("Abbrechen", role: .cancel) { confirmRestore = nil }
            }
        } message: {
            Text("Die aktuelle Version wird als neue Version archiviert; du kannst also nichts verlieren.")
        }
        .alert("Fehler", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
        } message: { Text(error ?? "") }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { items = try await api.listFileVersions(fileId) }
        catch let ex { error = ex.localizedDescription }
    }

    private func restore(_ versionId: UUID) async {
        guard let api = auth.api else { return }
        confirmRestore = nil
        do {
            try await api.restoreFileVersion(fileId: fileId, versionId: versionId)
            await load()
        } catch let ex { error = ex.localizedDescription }
    }
}
