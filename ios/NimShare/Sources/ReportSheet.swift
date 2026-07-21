import SwiftUI

/// v1.10.82: Meldung-Sheet — App-Store-Blocker Apple 1.2. Jede Ansicht
/// die fremde User-Inhalte zeigt (File-Details, Public Landing, Direct-
/// Share, Contact-Detail, Chat-Message) muss einen „Melden"-Weg haben.
/// Dieser Sheet ist der zentrale Presenter.
struct ReportSheet: View {
    let subjectKind: NimShareAPI.ReportSubjectKind
    let subjectId: UUID
    /// Menschlich lesbarer Anker (Dateiname, Slug, …) für die Admin-Queue.
    let subjectLabel: String?
    /// User dem die Ressource gehört — für Kontext + optionalen Block-Toggle.
    let subjectOwnerUserId: UUID?
    let subjectOwnerName: String?

    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    @State private var reason: NimShareAPI.ReportReason = .spam
    @State private var note = ""
    @State private var alsoBlock = false
    @State private var sending = false
    @State private var done = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section("Was ist das Problem?") {
                    Picker("Grund", selection: $reason) {
                        ForEach(NimShareAPI.ReportReason.allCases) { r in
                            Text(r.germanLabel).tag(r)
                        }
                    }
                    .pickerStyle(.inline)
                    .labelsHidden()
                }
                Section("Details (optional)") {
                    TextField("Weitere Angaben — was ist passiert?", text: $note, axis: .vertical)
                        .lineLimit(3...6)
                }
                if let ownerId = subjectOwnerUserId, ownerId != UUID() {
                    Section {
                        Toggle(isOn: $alsoBlock) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text("\(subjectOwnerName ?? "Nutzer") auch blockieren")
                                Text("Du siehst danach keine Direct-Shares oder Kontakte mehr von diesem Nutzer.")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
                if let e = error {
                    Section { Text(e).foregroundStyle(Theme.warnRed).font(.footnote) }
                }
            }
            .navigationTitle("Melden")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Senden") { Task { await send() } }
                        .disabled(sending)
                }
            }
            .overlay {
                if done {
                    VStack(spacing: 12) {
                        Image(systemName: "checkmark.circle.fill")
                            .font(.system(size: 48)).foregroundStyle(.green)
                        Text("Meldung ist eingegangen.")
                        Text("Ein Admin sichtet den Report zeitnah.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(32).background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
                }
                if sending { ProgressView() }
            }
        }
    }

    private func send() async {
        guard let api = auth.api else { return }
        sending = true; error = nil; defer { sending = false }
        do {
            try await api.reportContent(kind: subjectKind, subjectId: subjectId,
                reason: reason,
                note: note.isEmpty ? nil : note,
                subjectLabel: subjectLabel,
                subjectOwnerUserId: subjectOwnerUserId)
            if alsoBlock, let ownerId = subjectOwnerUserId {
                try? await api.blockUser(ownerId, reason: reason.germanLabel)
            }
            done = true
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            dismiss()
        } catch let ex {
            error = ex.localizedDescription
        }
    }
}
