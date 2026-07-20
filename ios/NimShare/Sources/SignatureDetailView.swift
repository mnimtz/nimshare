import SwiftUI
import QuickLook

/// v1.10.56 iOS: Detail-Screen für einen Signatur-Vorgang. Zeigt Status,
/// Beteiligte und die drei Actions die Marcus als Web-Feature v1.10.40
/// bekommen hat:
///  - 📥 Signiertes PDF öffnen (Completed)
///  - 🏁 Abschluss erzwingen (Sent)  → zeigt fehlende Beteiligte oder
///    triggert den Finalizer synchron
///  - 🗑 Löschen
struct SignatureDetailView: View {
    let requestId: UUID
    let initialTitle: String
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    @State private var detail: SignatureRequestDto?
    @State private var loading = true
    @State private var error: String?
    @State private var busy = false
    @State private var pdfURL: URL?
    @State private var showPdfQuickLook = false
    @State private var showFinalizeInfo = false
    @State private var finalizeInfo: String = ""
    @State private var showDeleteConfirm = false

    var body: some View {
        Group {
            if loading && detail == nil {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let d = detail {
                content(d)
            } else if let e = error {
                ContentUnavailableView(
                    "Konnte nicht laden",
                    systemImage: "exclamationmark.triangle",
                    description: Text(e)
                )
            } else {
                EmptyView()
            }
        }
        .navigationTitle(detail?.title ?? initialTitle)
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .refreshable { await load() }
        .quickLookPreview($pdfURL)
        .alert("Abschluss-Info", isPresented: $showFinalizeInfo) {
            Button("OK", role: .cancel) { }
        } message: {
            Text(finalizeInfo)
        }
        .confirmationDialog(
            "Signatur-Vorgang wirklich löschen?",
            isPresented: $showDeleteConfirm,
            titleVisibility: .visible
        ) {
            Button("Löschen", role: .destructive) { Task { await deleteRequest() } }
            Button("Abbrechen", role: .cancel) { }
        }
    }

    @ViewBuilder
    private func content(_ d: SignatureRequestDto) -> some View {
        List {
            Section {
                HStack {
                    statusBadge(d.status)
                    Spacer()
                    Text("\(signedCount(d)) / \(d.participants.count) fertig")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Text(d.sourceFileName).font(.footnote).foregroundStyle(.secondary)
                Text("Erstellt: \(d.createdAt.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption2).foregroundStyle(.secondary)
                if let sent = d.sentAt {
                    Text("Gesendet: \(sent.formatted(date: .abbreviated, time: .shortened))")
                        .font(.caption2).foregroundStyle(.secondary)
                }
                if let done = d.completedAt {
                    Text("Abgeschlossen: \(done.formatted(date: .abbreviated, time: .shortened))")
                        .font(.caption2).foregroundStyle(.secondary)
                }
            }
            Section("Beteiligte") {
                ForEach(d.participants.sorted { $0.order < $1.order }) { p in
                    HStack(alignment: .top) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(p.name).font(.body.weight(.medium))
                            Text(p.email).font(.caption).foregroundStyle(.secondary)
                            Text("\(p.role) · \(p.status)")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                        Spacer()
                        participantIcon(p)
                    }
                    .padding(.vertical, 2)
                }
            }
            Section("Aktionen") {
                if d.status == "Completed", d.finalFileId != nil {
                    Button {
                        Task { await openSignedPdf() }
                    } label: {
                        Label("Signiertes PDF öffnen", systemImage: "doc.text.fill")
                    }
                    .disabled(busy)
                }
                if d.status == "Sent" {
                    Button {
                        Task { await forceFinalize() }
                    } label: {
                        Label("Abschluss erzwingen", systemImage: "flag.checkered")
                    }
                    .disabled(busy)
                    Button(role: .destructive) {
                        Task { await cancelRequest() }
                    } label: {
                        Label("Vorgang abbrechen", systemImage: "xmark.circle")
                    }
                    .disabled(busy)
                }
                Button(role: .destructive) {
                    showDeleteConfirm = true
                } label: {
                    Label("Löschen", systemImage: "trash")
                }
                .disabled(busy)
            }
            if let e = error {
                Section {
                    Text(e).font(.footnote).foregroundStyle(Theme.warnRed)
                }
            }
        }
        .overlay {
            if busy { ProgressView().padding().background(.thickMaterial).clipShape(RoundedRectangle(cornerRadius: 10)) }
        }
    }

    private func signedCount(_ d: SignatureRequestDto) -> Int {
        d.participants.filter { $0.status == "Signed" || ($0.role == "Viewer" && $0.status == "Viewed") }.count
    }

    private func statusBadge(_ status: String) -> some View {
        let (color, label): (Color, String)
        switch status {
        case "Completed": (color, label) = (.green, "Abgeschlossen")
        case "Sent":      (color, label) = (.orange, "Läuft")
        case "Declined":  (color, label) = (Theme.warnRed, "Abgelehnt")
        case "Cancelled": (color, label) = (.gray, "Abgebrochen")
        default:          (color, label) = (.gray, "Entwurf")
        }
        return Text(label)
            .font(.caption.weight(.semibold))
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(color.opacity(0.15))
            .foregroundStyle(color)
            .clipShape(Capsule())
    }

    @ViewBuilder
    private func participantIcon(_ p: SignatureParticipantDto) -> some View {
        switch p.status {
        case "Signed":
            Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case "Viewed":
            Image(systemName: "eye.fill").foregroundStyle(.blue)
        case "Declined":
            Image(systemName: "xmark.circle.fill").foregroundStyle(Theme.warnRed)
        default:
            Image(systemName: "circle").foregroundStyle(.secondary)
        }
    }

    // MARK: - Actions

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do { detail = try await api.signatureRequestDetail(requestId) }
        catch let ex { error = ex.localizedDescription }
    }

    private func openSignedPdf() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            let (data, filename) = try await api.downloadSignedPdf(requestId)
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(filename)
            try data.write(to: tmp, options: .atomic)
            pdfURL = tmp
        } catch let ex {
            error = "PDF konnte nicht geladen werden: \(ex.localizedDescription)"
        }
    }

    private func forceFinalize() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            let resp = try await api.forceFinalizeSignature(requestId)
            if resp.status == "Completed" {
                finalizeInfo = "Vorgang wurde soeben abgeschlossen. Das signierte PDF ist jetzt verfügbar."
                await load()
            } else if let pending = resp.pending, !pending.isEmpty {
                let lines = pending.map { "• \($0.name ?? $0.email ?? "?") (\($0.role ?? "?")) — \($0.status ?? "?")" }
                finalizeInfo = "Kann noch nicht abgeschlossen werden — folgende Beteiligte fehlen:\n\n" + lines.joined(separator: "\n")
            } else {
                finalizeInfo = "Finalizer lief, aber der Vorgang ist nicht abgeschlossen.\n\n" + (resp.note ?? "")
            }
            showFinalizeInfo = true
        } catch let ex {
            error = "Finalize fehlgeschlagen: \(ex.localizedDescription)"
        }
    }

    private func cancelRequest() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            try await api.cancelSignatureRequest(requestId)
            await load()
        } catch let ex {
            error = "Abbrechen fehlgeschlagen: \(ex.localizedDescription)"
        }
    }

    private func deleteRequest() async {
        guard let api = auth.api else { return }
        busy = true; defer { busy = false }
        do {
            try await api.deleteSignatureRequest(requestId)
            dismiss()
        } catch let ex {
            error = "Löschen fehlgeschlagen: \(ex.localizedDescription)"
        }
    }
}
