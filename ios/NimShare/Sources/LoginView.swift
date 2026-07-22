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
    @State private var rememberCredentials = true
    @FocusState private var focusedField: Field?

    enum Field { case email, password }

    var body: some View {
        // v1.10.69: kräftiger Tungsten-Branded Header. Marcus's Feedback:
        // "startseite eher immer noch weiß". Jetzt: oberes Drittel in
        // Tungsten-Blau als Gradient (dunkel→hell), Logo+Titel weiß auf
        // blau (Cloud-Icon fällt vor blauem Hintergrund weg — daher
        // System-Icon in weißem Kreis als Fallback + Text drüber), weißer
        // Content-Bereich mit Login-Card darunter fließt in einer
        // Wave-Kurve rein für den Look. Kein reines Weiß mehr.
        ZStack(alignment: .top) {
            // Voll-Hintergrund: weiß unten, Tungsten oben.
            LinearGradient(
                colors: [Theme.tungstenDark, Theme.tungstenBlue],
                startPoint: .top,
                endPoint: .bottom
            )
            .frame(height: 320)
            .ignoresSafeArea(edges: .top)
            .frame(maxHeight: .infinity, alignment: .top)

            ScrollView(showsIndicators: false) {
                VStack(spacing: 28) {
                    // Header auf blauem Hintergrund — Logo, Titel + Subtitle
                    // in weiß gerendert (contrast pop).
                    VStack(spacing: 12) {
                        Image("AppLogo")
                            .resizable()
                            .scaledToFit()
                            .frame(maxWidth: 110, maxHeight: 110)
                            .shadow(color: .black.opacity(0.28), radius: 14, x: 0, y: 6)
                            .accessibilityHidden(true)
                        Text("NimShare")
                            .font(.system(size: 32, weight: .bold, design: .default))
                            .foregroundStyle(.white)
                        Text("Sichere Datenübergabe")
                            .font(.subheadline)
                            .foregroundStyle(Color.white.opacity(0.85))
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

                        // v1.10.64 — kompakter "Merken"-Tap. Marcus's Feedback
                        // zu v1.10.63: der button-style Toggle war zu prominent
                        // ("normal eher mittig und kleiner"). Neu: kleines
                        // Checkbox-Icon + grauer Footnote-Text, zentriert unter
                        // dem Login-Button — die klassische Login-Konvention.
                        Button {
                            rememberCredentials.toggle()
                        } label: {
                            HStack(spacing: 6) {
                                Image(systemName: rememberCredentials ? "checkmark.square.fill" : "square")
                                    .font(.caption)
                                    .foregroundStyle(rememberCredentials ? Theme.tungstenBlue : Color.secondary)
                                Text("Anmeldedaten merken")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .buttonStyle(.plain)
                        .frame(maxWidth: .infinity, alignment: .center)
                        .padding(.top, 6)

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
                            Text(auth.serverURL?.host ?? "nimshare.com")
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
            // v1.10.69: E-Mail vorausfüllen wenn merken=on, aber KEINEN
            // Focus setzen — sonst poppt die Tastatur sofort beim Screen-
            // Öffnen (Marcus's Kritik "blöde Tastatur nicht immer sofort
            // da"). User tippt jetzt selbst wenn er will.
            if let last = auth.lastEmail, email.isEmpty {
                email = last
            }
            // Toggle-Zustand aus letzter Session übernehmen.
            rememberCredentials = auth.rememberCredentials
        }
        .onChange(of: rememberCredentials) { _, newValue in
            // Persistieren damit beim nächsten App-Start der letzte
            // Zustand vorausgewählt ist.
            auth.rememberCredentials = newValue
            if !newValue {
                // Toggle deaktiviert → gespeicherte E-Mail sofort löschen
                // (nicht erst beim nächsten Login).
                auth.lastEmail = nil
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
            // v1.10.63: nach erfolgreichem Login den Toggle-Zustand anwenden.
            // AuthStore.login speichert lastEmail immer — wenn der User
            // "Merken" abgewählt hat, löschen wir es hier direkt wieder.
            if !rememberCredentials {
                auth.lastEmail = nil
            }
        } catch let e as ApiError {
            error = e.localizedDescription
        } catch let ex {
            error = ex.localizedDescription
        }
    }
}
