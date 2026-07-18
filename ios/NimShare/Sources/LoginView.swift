import SwiftUI

struct LoginView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var email = ""
    @State private var password = ""
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        VStack(spacing: 24) {
            Spacer()
            Image("AppLogo").resizable().scaledToFit().frame(width: 180)
                .accessibilityHidden(true)
            Text("Sign in").font(.title.bold())
            if let host = auth.serverURL?.host {
                Text(host).font(.footnote).foregroundStyle(.secondary)
            }
            VStack(spacing: 12) {
                TextField("Email", text: $email)
                    .textFieldStyle(.roundedBorder)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.emailAddress)
                    .textContentType(.emailAddress)
                SecureField("Password", text: $password)
                    .textFieldStyle(.roundedBorder)
                    .textContentType(.password)
            }
            if let e = error {
                Text(e).font(.footnote).foregroundStyle(Theme.warnRed)
                    .multilineTextAlignment(.center)
            }
            Button {
                Task { await doLogin() }
            } label: {
                if busy { ProgressView() } else { Text("Sign in").frame(maxWidth: .infinity) }
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(email.isEmpty || password.isEmpty || busy)

            Button("Change server", action: auth.changeServer)
                .font(.footnote)
                .foregroundStyle(.secondary)
            Spacer()
        }
        .padding()
    }

    private func doLogin() async {
        busy = true; error = nil
        defer { busy = false }
        do {
            try await auth.login(email: email, password: password)
        } catch let e as ApiError {
            error = e.localizedDescription
        } catch let ex {
            error = ex.localizedDescription
        }
    }
}
