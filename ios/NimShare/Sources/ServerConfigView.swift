import SwiftUI

struct ServerConfigView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var input = "https://"
    @State private var error: String?

    var body: some View {
        VStack(spacing: 24) {
            Spacer()
            Image("AppLogo").resizable().scaledToFit().frame(width: 200)
                .accessibilityHidden(true)
            Text("NimShare").font(.largeTitle.bold())
            Text("Enter your NimShare server URL")
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 8) {
                TextField("https://nimshare.example.com", text: $input)
                    .textFieldStyle(.roundedBorder)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.URL)
                if let e = error {
                    Text(e).font(.footnote).foregroundStyle(Theme.warnRed)
                }
            }
            Button("Continue") { save() }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(!isValid)
            Spacer()
        }
        .padding()
    }

    private var isValid: Bool {
        guard let u = URL(string: input.trimmingCharacters(in: .whitespaces)), let s = u.scheme else { return false }
        return (s == "http" || s == "https") && u.host != nil
    }

    private func save() {
        guard let u = URL(string: input.trimmingCharacters(in: .whitespaces)) else { return }
        auth.setServer(u)
    }
}
