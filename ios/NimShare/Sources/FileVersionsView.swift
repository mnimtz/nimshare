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
            } else if items.isEmpty && error != nil {
                // v1.11.55: bei einem echten Ladefehler zeigte diese Ansicht
                // vorher trotzdem "Diese Datei hat nur eine Version" — eine
                // falsche Tatsachenbehauptung, obwohl schlicht der Request
                // fehlgeschlagen ist (der Fehler landete nur im separaten Alert).
                RSEmptyState(systemImage: "exclamationmark.triangle", title: "Fehler beim Laden", desc: error ?? "")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                RSEmptyState(systemImage: "clock.arrow.circlepath", title: "Keine Versionen",
                    desc: "Diese Datei hat nur eine Version. Beim erneuten Upload wird die alte hier archiviert.")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(items) { v in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text("v\(v.versionNumber)").font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                                if v.isCurrent {
                                    Chip(text: "Aktuell", color: Theme.success2, bg: Theme.success2.opacity(0.12))
                                }
                                Spacer()
                                Text(ByteCountFormatter.string(fromByteCount: v.sizeBytes, countStyle: .file))
                                    .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                            }
                            Text(v.createdAt.formatted(date: .abbreviated, time: .shortened))
                                .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                            Text("Hochgeladen von: \(v.createdByName)").font(TFont.caption).foregroundStyle(Theme.textTertiary)
                        }
                        .padding(.vertical, 4)
                        .listRowBackground(Theme.surface2)
                        .listRowSeparator(.hidden)
                        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                            if !v.isCurrent {
                                Button {
                                    confirmRestore = v
                                } label: { Label("Wiederherstellen", systemImage: "arrow.uturn.backward") }
                                    .tint(Theme.cyan)
                            }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
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
        catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }

    private func restore(_ versionId: UUID) async {
        guard let api = auth.api else { return }
        confirmRestore = nil
        do {
            try await api.restoreFileVersion(fileId: fileId, versionId: versionId)
            await load()
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ } catch let ex { error = ex.localizedDescription }
    }
}
