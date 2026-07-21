import SwiftUI

/// v1.10.59: ServerConfigView kann jetzt sowohl als Vollbild-Setup
/// (`isSheet: false`) als auch als Modal-Sheet (`isSheet: true`) gezeigt
/// werden. Sheet-Modus bekommt eine Navigation-Toolbar mit Fertig-Button.
/// Der Standard-Server wird als Placeholder vorausgefüllt, damit der User
/// sofort sieht was er ändern würde.
struct ServerConfigView: View {
    @EnvironmentObject var auth: AuthStore
    var isSheet: Bool = false
    var onDone: (() -> Void)? = nil

    @State private var input: String = ""
    @State private var error: String?

    var body: some View {
        content
            .navigationTitle(isSheet ? "Server ändern" : "")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                if isSheet {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Abbrechen") { onDone?() }
                    }
                }
            }
    }

    @ViewBuilder
    private var content: some View {
        ZStack {
            LinearGradient(
                colors: [
                    Theme.tungstenBlue.opacity(isSheet ? 0 : 0.12),
                    Color(.systemBackground)
                ],
                startPoint: .top,
                endPoint: .center
            )
            .ignoresSafeArea()

            ScrollView(showsIndicators: false) {
                VStack(spacing: 22) {
                    if !isSheet {
                        Image("AppLogo")
                            .resizable().scaledToFit()
                            .frame(maxWidth: 220, maxHeight: 90)
                            .padding(.top, 60)
                        Text("NimShare")
                            .font(.system(size: 30, weight: .bold))
                            .foregroundStyle(Theme.tungstenDark)
                    }
                    Text(isSheet
                         ? "Trage die URL deiner eigenen NimShare-Instanz ein."
                         : "NimShare-Server-URL")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 20)

                    VStack(spacing: 14) {
                        HStack(spacing: 12) {
                            Image(systemName: "server.rack")
                                .foregroundStyle(.secondary)
                                .frame(width: 20)
                            TextField(AuthStore.defaultServerURL.absoluteString, text: $input)
                                .textInputAutocapitalization(.never)
                                .autocorrectionDisabled()
                                .keyboardType(.URL)
                                .textContentType(.URL)
                                .textFieldStyle(.plain)
                        }
                        .padding(14)
                        .background(Color(.secondarySystemBackground))
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                        .overlay(
                            RoundedRectangle(cornerRadius: 10)
                                .stroke(Color.gray.opacity(0.15), lineWidth: 1)
                        )

                        if let e = error {
                            Text(e).font(.footnote).foregroundStyle(Theme.warnRed)
                        }

                        Button {
                            save()
                        } label: {
                            HStack(spacing: 8) {
                                Image(systemName: "checkmark.circle.fill")
                                Text("Speichern")
                                    .font(.body.weight(.semibold))
                            }
                            .frame(maxWidth: .infinity, minHeight: 52)
                        }
                        .buttonStyle(.plain)
                        .background(isValid ? Theme.tungstenBlue : Color.gray.opacity(0.3))
                        .foregroundStyle(.white)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                        .disabled(!isValid)

                        if input != AuthStore.defaultServerURL.absoluteString {
                            Button("Standard wiederherstellen") {
                                input = AuthStore.defaultServerURL.absoluteString
                            }
                            .font(.footnote)
                            .foregroundStyle(Theme.tungstenBlue)
                            .padding(.top, 4)
                        }
                    }
                    .padding(22)
                    .background(Color(.systemBackground))
                    .clipShape(RoundedRectangle(cornerRadius: 20))
                    .shadow(color: Color.black.opacity(0.06), radius: 20, x: 0, y: 4)
                    .padding(.horizontal, 20)

                    Spacer(minLength: 20)
                }
                .frame(maxWidth: .infinity)
            }
        }
        .onAppear {
            if input.isEmpty {
                // Wenn der User schon eine URL gesetzt hat → die anzeigen,
                // sonst den Default vorbelegen.
                input = auth.serverURL?.absoluteString ?? AuthStore.defaultServerURL.absoluteString
            }
        }
    }

    private var isValid: Bool {
        let trimmed = input.trimmingCharacters(in: .whitespaces)
        guard let u = URL(string: trimmed), let s = u.scheme else { return false }
        return (s == "http" || s == "https") && u.host != nil
    }

    private func save() {
        let trimmed = input.trimmingCharacters(in: .whitespaces)
        guard let u = URL(string: trimmed) else {
            error = "Ungültige URL"
            return
        }
        auth.setServer(u)
        onDone?()
    }
}
