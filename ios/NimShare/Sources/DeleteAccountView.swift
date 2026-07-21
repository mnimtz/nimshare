import SwiftUI

/// v1.10.82: App-Store-Blocker (Apple Guideline 5.1.1(v)) — Account-Löschung
/// muss IN-APP möglich sein, nicht nur im Web. Ohne diesen Screen kein
/// Approval. Zwei-Stufen-Confirmation (Passwort + explizite Bestätigung)
/// gegen versehentliches Löschen.
struct DeleteAccountView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    @State private var password = ""
    @State private var confirmationText = ""
    @State private var showFinalAlert = false
    @State private var loading = false
    @State private var error: String?

    private let requiredConfirmation = "LÖSCHEN"

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Label("Diese Aktion ist endgültig.", systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(Theme.warnRed)
                            .font(.body.weight(.semibold))
                        Text("Wenn du deinen NimShare-Account löschst, verlierst du dauerhaft:")
                            .font(.footnote)
                        Text("• alle Dateien in deinen persönlichen Ordnern\n• alle Datei-Versionen und Kommentare\n• deine Kontakte, API-Tokens, Zertifikate\n• deine Signaturvorgänge (als Ersteller)\n• Direct-Shares von und an dich")
                            .font(.footnote).foregroundStyle(.secondary)
                        Text("Der Account kann NICHT wiederhergestellt werden.")
                            .font(.footnote.weight(.semibold))
                            .foregroundStyle(Theme.warnRed)
                    }
                }
                Section("Passwort bestätigen") {
                    SecureField("Aktuelles Passwort", text: $password)
                        .textContentType(.password)
                }
                Section("Bestätigung") {
                    // v1.10.91: extended delimiters für das „…"
                    Text(#"Zum Bestätigen bitte „\#(requiredConfirmation)" eintippen:"#)
                        .font(.footnote).foregroundStyle(.secondary)
                    TextField(requiredConfirmation, text: $confirmationText)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.characters)
                }
                if let e = error {
                    Section {
                        Text(e).foregroundStyle(Theme.warnRed).font(.footnote)
                    }
                }
                Section {
                    Button(role: .destructive) {
                        showFinalAlert = true
                    } label: {
                        Label("Account jetzt löschen", systemImage: "trash.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .disabled(confirmationText.uppercased() != requiredConfirmation || password.isEmpty || loading)
                }
            }
            .navigationTitle("Account löschen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
            }
            .overlay { if loading { ProgressView().padding().background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12)) } }
            .alert("Wirklich unwiderruflich löschen?", isPresented: $showFinalAlert) {
                Button("Endgültig löschen", role: .destructive) {
                    Task { await performDelete() }
                }
                Button("Abbrechen", role: .cancel) { }
            } message: {
                Text("Alle deine Dateien und Daten werden sofort und unwiderruflich vom Server entfernt.")
            }
        }
    }

    private func performDelete() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do {
            _ = try await api.deleteMyAccount(password: password)
            // Server ist durch, lokale Session weg, zurück auf Login.
            auth.signOut()
            dismiss()
        } catch let ex {
            error = ex.localizedDescription
        }
    }
}
