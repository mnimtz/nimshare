import SwiftUI

struct TotpChallengeView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var code = ""
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        VStack(spacing: 20) {
            Spacer()
            Image(systemName: "lock.shield.fill")
                .font(.system(size: 60))
                .foregroundStyle(Theme.navyFg)
            Text("2FA-Code eingeben")
                .font(TFont.titleL)
                .foregroundStyle(Theme.textPrimary)
            Text("Öffne deine Authenticator-App und gib den 6-stelligen Code ein.")
                .font(TFont.bodyM)
                .foregroundStyle(Theme.textSecondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 40)

            TextField("123456", text: $code)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .multilineTextAlignment(.center)
                .font(.system(size: 32, weight: .medium, design: .monospaced))
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 10).fill(Theme.surfaceMuted))
                .padding(.horizontal, 40)
                .onChange(of: code) { _, new in
                    if new.count > 6 { code = String(new.prefix(6)) }
                    if code.count == 6 { Task { await submit() } }
                }

            if let e = error {
                Text(e).font(TFont.bodyS).foregroundStyle(Theme.danger2)
                    .multilineTextAlignment(.center)
            }

            Button {
                Task { await submit() }
            } label: {
                if busy { ProgressView() } else { Text("Bestätigen").frame(maxWidth: .infinity) }
            }
            .buttonStyle(.borderedProminent)
            .tint(Theme.navy)
            .controlSize(.large)
            .disabled(code.count != 6 || busy)
            .padding(.horizontal, 40)

            Button("Abbrechen", role: .cancel) { auth.cancelTotpChallenge() }
                .font(TFont.bodyS)
                .foregroundStyle(Theme.textSecondary)
            Spacer()
        }
        .padding()
        .background(Theme.bgGradient.ignoresSafeArea())
    }

    private func submit() async {
        // v1.11.55: onChange(of: code) mutiert `code` selbst beim Truncaten
        // (Paste/Autofill mit >6 Zeichen) — SwiftUI feuert onChange dafür
        // ein zweites Mal, was submit() zweimal parallel auslösen konnte.
        guard !busy else { return }
        busy = true; error = nil
        defer { busy = false }
        do {
            try await auth.completeTotpLogin(code: code)
        } catch let e as ApiError {
            error = e.localizedDescription
            code = ""
        } catch let ex {
            error = ex.localizedDescription
            code = ""
        }
    }
}
