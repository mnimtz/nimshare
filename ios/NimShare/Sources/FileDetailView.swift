import SwiftUI

/// v1.11.72 (Redesign-Pilot): neuer Datei-Detail-Screen zwischen Datei-Liste
/// und der eigentlichen QuickLook-Vorschau. Im Handoff-Prototyp ein
/// eigenständiger Screen-Typ (Vorschau-Kachel + 4er-Action-Grid + KI-
/// Erkenntnisse + Metadaten) — kein Restyling von FilePreviewView. Die
/// bleibt unverändert der reine QuickLook-Wrapper und wird von hier per
/// „Vorschau" aufgerufen. „KI-Erkenntnisse" nutzt bewusst die bereits
/// vorhandenen, echten aiTags/aiRiskFlag-Felder statt einer neuen
/// Zusammenfassungs-API — es gibt (noch) keinen Server-Endpoint für eine
/// echte Freitext-Zusammenfassung pro Datei.
struct FileDetailView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let file: FileItem

    @State private var showQuickLook = false
    @State private var shareTarget: ShareLinkCreateSheet.Target?
    @State private var shareItemName: String = ""
    @State private var downloading = false
    @State private var favBusy = false
    @State private var isFavorite = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: Theme.Space.xl) {
                    previewTile
                    actionGrid
                    if !file.tags.isEmpty || (file.aiRiskFlag?.isEmpty == false) {
                        aiInsights
                    }
                    metadata
                }
                .padding(Theme.Space.lg)
            }
            .background(Theme.bgGradient.ignoresSafeArea())
            .navigationTitle(file.name)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Schließen") { dismiss() }
                }
            }
        }
        .sheet(isPresented: $showQuickLook) {
            NavigationStack { FilePreviewView(file: file) }
        }
        .sheet(item: $shareTarget) { t in
            ShareLinkCreateSheet(target: t, itemName: shareItemName)
        }
    }

    // MARK: - Vorschau-Kachel

    private var previewTile: some View {
        VStack(spacing: Theme.Space.md) {
            Button { showQuickLook = true } label: {
                FileThumbView(file: file, iconSize: 72)
                    .frame(width: 168, height: 168)
            }
            .buttonStyle(.plain)
            Text(file.name)
                .font(TFont.titleM)
                .foregroundStyle(Theme.textPrimary)
                .multilineTextAlignment(.center)
                .lineLimit(3)
            Text(subtitle)
                .font(TFont.bodyS)
                .foregroundStyle(Theme.textSecondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.top, Theme.Space.md)
    }

    private var subtitle: String {
        let size = ByteCountFormatter.string(fromByteCount: file.sizeBytes, countStyle: .file)
        let date = file.createdAt.formatted(date: .abbreviated, time: .omitted)
        return "\(size) · \(date)"
    }

    // MARK: - Action-Grid

    private var actionGrid: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: Theme.Space.md), count: 4),
                   spacing: Theme.Space.md) {
            actionButton(icon: "eye", label: "Vorschau") { showQuickLook = true }
            actionButton(icon: "link.badge.plus", label: "Teilen") {
                shareItemName = file.name
                shareTarget = .file(file.id)
            }
            actionButton(icon: "arrow.down.circle", label: "Laden", busy: downloading) {
                Task { await download() }
            }
            actionButton(icon: isFavorite ? "star.fill" : "star", label: "Favorit", busy: favBusy) {
                Task { await toggleFav() }
            }
        }
    }

    @ViewBuilder
    private func actionButton(icon: String, label: String, busy: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 6) {
                ZStack {
                    Circle().fill(Theme.surface2).frame(width: 52, height: 52)
                        .overlay(Circle().stroke(Theme.border2, lineWidth: 1))
                    if busy {
                        ProgressView().controlSize(.mini)
                    } else {
                        Image(systemName: icon).font(.system(size: 19)).foregroundStyle(Theme.navy)
                    }
                }
                Text(label).font(TFont.caption).foregroundStyle(Theme.textSecondary)
            }
        }
        .buttonStyle(.plain)
        .disabled(busy)
    }

    // MARK: - KI-Erkenntnisse (echte aiTags/aiRiskFlag-Daten)

    private var aiInsights: some View {
        VStack(alignment: .leading, spacing: Theme.Space.s) {
            RSSectionHeader(title: "KI-Erkenntnisse")
            if let risk = file.aiRiskFlag, !risk.isEmpty {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Theme.danger2)
                    Text(risk).font(TFont.bodyS).foregroundStyle(Theme.textPrimary)
                }
            }
            if !file.tags.isEmpty {
                HStack(spacing: 6) {
                    ForEach(file.tags, id: \.self) { tag in
                        Chip(text: tag, color: Theme.cyan, bg: Theme.cyan.opacity(0.12))
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .rsCard()
    }

    // MARK: - Metadaten

    private var metadata: some View {
        VStack(alignment: .leading, spacing: Theme.Space.s) {
            RSSectionHeader(title: "Details")
            metaRow("Typ", file.contentType)
            metaRow("Größe", ByteCountFormatter.string(fromByteCount: file.sizeBytes, countStyle: .file))
            metaRow("Erstellt", file.createdAt.formatted(date: .long, time: .shortened))
            if let owner = file.ownerName, !owner.isEmpty {
                metaRow("Besitzer", owner)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .rsCard()
    }

    private func metaRow(_ label: String, _ value: String) -> some View {
        HStack(alignment: .top) {
            Text(label).font(TFont.bodyS).foregroundStyle(Theme.textSecondary)
            Spacer()
            Text(value).font(TFont.bodyS.weight(.semibold)).foregroundStyle(Theme.textPrimary)
                .multilineTextAlignment(.trailing)
        }
    }

    // MARK: - Actions (eigenständig, wie FilePreviewView — kein State-Sharing mit dem Elternscreen)

    private func download() async {
        guard let api = auth.api else { return }
        downloading = true; defer { downloading = false }
        do {
            let r = try await api.previewUrl(fileId: file.id)
            guard let src = URL(string: r.url) else { throw ApiError.network("Bad URL") }
            let (tmp, _) = try await URLSession.shared.download(from: src)
            let dest = TmpFile.destinationURL(for: file.name)
            try FileManager.default.moveItem(at: tmp, to: dest)
            await MainActor.run { TmpFile.presentShareSheet(for: [dest]) }
        } catch is CancellationError { /* Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }

    private func toggleFav() async {
        guard let api = auth.api else { return }
        favBusy = true; defer { favBusy = false }
        do {
            isFavorite = try await api.toggleFavorite(fileId: file.id)
        } catch is CancellationError { /* Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }
}
