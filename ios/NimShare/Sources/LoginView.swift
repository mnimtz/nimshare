import SwiftUI

/// v1.10.59: Neu-Design nach Marcus's Vorgabe. Statt "weißer Kasten mit
/// einer Zeile" jetzt eine gestaltete Willkommens-Seite mit Tungsten-Logo,
/// klarem Titel, gestylten Eingabefeldern, Keychain-AutoFill für E-Mail +
/// Passwort und "Server ändern" als kleiner Link ganz unten.
struct LoginView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var email = ""
    @State private var password = ""
    @State private var busy = false
    @State private var error: String?
    @State private var showServerSheet = false
    @FocusState private var focusedField: Field?

    enum Field { case email, password }

    var body: some View {
        ZStack {
            // Subtiler Tungsten-Gradient statt reinem Weiß — gibt dem
            // Screen visuelles Gewicht ohne zu dominieren.
            LinearGradient(
                colors: [
                    Theme.tungstenBlue.opacity(0.12),
                    Color(.systemBackground),
                    Color(.systemBackground)
                ],
                startPoint: .top,
                endPoint: .center
            )
            .ignoresSafeArea()

            ScrollView(showsIndicators: false) {
                VStack(spacing: 28) {
                    // Header: Logo + Titel + Subtitle
                    VStack(spacing: 14) {
                        Image("AppLogo")
                            .resizable()
                            .scaledToFit()
                            .frame(maxWidth: 220, maxHeight: 90)
                            .accessibilityHidden(true)
                        Text("NimShare")
                            .font(.system(size: 30, weight: .bold, design: .default))
                            .foregroundStyle(Theme.tungstenDark)
                        Text("Sichere Datenübergabe")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .padding(.top, 50)

                    // Login-Card
                    VStack(spacing: 14) {
                        // E-Mail-Feld
                        fieldBox(icon: "envelope") {
                            TextField("E-Mail", text: $email)
                                .textInputAutocapitalization(.never)
                                .autocorrectionDisabled()
                                .keyboardType(.emailAddress)
                                .textContentType(.username)
                                .submitLabel(.next)
                                .focused($focusedField, equals: .email)
                                .onSubmit { focusedField = .password }
                        }

                        // Passwort-Feld
                        fieldBox(icon: "lock") {
                            SecureField("Passwort", text: $password)
                                .textContentType(.password)
                                .submitLabel(.go)
                                .focused($focusedField, equals: .password)
                                .onSubmit {
                                    if !email.isEmpty && !password.isEmpty {
                                        Task { await doLogin() }
                                    }
                                }
                        }

                        if let e = error {
                            HStack(alignment: .top, spacing: 8) {
                                Image(systemName: "exclamationmark.triangle.fill")
                                    .foregroundStyle(Theme.warnRed)
                                Text(e)
                                    .font(.footnote)
                                    .foregroundStyle(Theme.warnRed)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .padding(12)
                            .background(Theme.warnRed.opacity(0.08))
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        }

                        // Login-Button — Tungsten-Blau, prominent
                        Button {
                            Task { await doLogin() }
                        } label: {
                            HStack(spacing: 8) {
                                if busy {
                                    ProgressView().tint(.white).scaleEffect(0.9)
                                } else {
                                    Image(systemName: "arrow.right.circle.fill")
                                }
                                Text(busy ? "Anmelden…" : "Anmelden")
                                    .font(.body.weight(.semibold))
                            }
                            .frame(maxWidth: .infinity, minHeight: 52)
                        }
                        .buttonStyle(.plain)
                        .background(loginEnabled ? Theme.tungstenBlue : Color.gray.opacity(0.3))
                        .foregroundStyle(.white)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                        .disabled(!loginEnabled)
                        .animation(.easeOut(duration: 0.15), value: loginEnabled)
                    }
                    .padding(22)
                    .background(Color(.systemBackground))
                    .clipShape(RoundedRectangle(cornerRadius: 20))
                    .shadow(color: Color.black.opacity(0.08), radius: 24, x: 0, y: 6)
                    .padding(.horizontal, 20)

                    // Server-Info + kleiner "Server ändern"-Link
                    VStack(spacing: 6) {
                        HStack(spacing: 6) {
                            Image(systemName: "server.rack")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                            Text(auth.serverURL?.host ?? "nimshare.azurewebsites.net")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Button("Server ändern") { showServerSheet = true }
                            .font(.caption)
                            .foregroundStyle(Theme.tungstenBlue)
                    }
                    .padding(.top, 20)

                    Spacer(minLength: 30)
                }
                .frame(maxWidth: .infinity)
            }
        }
        .onAppear {
            // E-Mail vorausfüllen für Wiederkehrer — Passwort kommt aus
            // dem iOS-Schlüsselbund via AutoFill wenn der User es beim
            // ersten Login gespeichert hat.
            if let last = auth.lastEmail, email.isEmpty {
                email = last
                focusedField = .password
            }
        }
        .sheet(isPresented: $showServerSheet) {
            NavigationStack {
                ServerConfigView(isSheet: true, onDone: { showServerSheet = false })
            }
        }
    }

    private var loginEnabled: Bool {
        !email.isEmpty && !password.isEmpty && !busy
    }

    /// Reusable field-container — Icon links, TextField oder SecureField rechts,
    /// abgerundeter Rahmen, Fokus-Highlight in Tungsten-Blau.
    @ViewBuilder
    private func fieldBox<Content: View>(icon: String, @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 12) {
            Image(systemName: icon)
                .foregroundStyle(.secondary)
                .frame(width: 20)
            content()
                .textFieldStyle(.plain)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 14)
        .background(Color(.secondarySystemBackground))
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .stroke(Color.gray.opacity(0.15), lineWidth: 1)
        )
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
