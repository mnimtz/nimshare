import SwiftUI

/// v1.10.165 — First-Use-Consent-Sheet für AI-Verarbeitung (Apple 5.1.1(i)).
/// Zeigt dem User klar: welche Daten gehen wohin, holt explizite Zustimmung.
/// Alle AI-Feature-Views (ChatView, SearchView, GreetingBanner) präsentieren
/// dieses Sheet, wenn `auth.aiConsented != true` beim ersten Klick.
struct AiConsentSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    let onDecided: (Bool) -> Void

    @State private var busy = false
    @State private var loadingProviderInfo = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    HStack(spacing: 12) {
                        Text("✨").font(.system(size: 40))
                        VStack(alignment: .leading, spacing: 2) {
                            Text("KI-Verarbeitung aktivieren?")
                                .font(.title3.weight(.semibold))
                            Text("Einmalige Zustimmung nach Apple-Richtlinie")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }

                    Text("KI-Funktionen wie Chat mit deinen Dateien, Zusammenfassungen und intelligente Suche verarbeiten deine Anfrage und relevante Ausschnitte aus deinen Dateien bei einem externen KI-Anbieter.")
                        .font(.body)

                    // v1.10.165: Provider-Info ist Apple-5.1.1(i)-Pflicht
                    // („specify who the data is sent to"). Wenn noch nicht
                    // geladen, hier Spinner statt Sheet ohne Empfänger-Namen
                    // — der „Erlauben"-Button ist während der Zeit disabled.
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Aktueller Anbieter dieser Instanz")
                            .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                        if let info = auth.aiProviderInfo {
                            HStack(spacing: 8) {
                                Text("🛰")
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(providerDisplayName(info.provider))
                                        .font(.body.weight(.semibold))
                                    if let m = info.model {
                                        Text("Modell: \(m)").font(.caption).foregroundStyle(.secondary)
                                    }
                                    if let h = info.endpointHint {
                                        Text("Endpoint: \(h)").font(.caption.monospaced()).foregroundStyle(.secondary)
                                    }
                                }
                            }
                            .padding(12)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(RoundedRectangle(cornerRadius: 10).fill(Color.gray.opacity(0.08)))
                        } else if loadingProviderInfo {
                            HStack(spacing: 8) {
                                ProgressView().controlSize(.small)
                                Text("Anbieter-Info wird geladen…").font(.caption).foregroundStyle(.secondary)
                            }
                            .padding(12)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(RoundedRectangle(cornerRadius: 10).fill(Color.gray.opacity(0.08)))
                        } else {
                            Text("Anbieter-Info nicht verfügbar. Bitte Netzwerk prüfen und erneut versuchen.")
                                .font(.caption).foregroundStyle(Theme.danger2)
                        }
                    }

                    VStack(alignment: .leading, spacing: 6) {
                        Text("Was gesendet wird")
                            .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                        Label("Deine Chat-Anfrage bzw. Suchanfrage", systemImage: "text.bubble")
                        Label("Text-Ausschnitte der Dateien, die für deine Anfrage relevant sind", systemImage: "doc.text")
                        Label("Kein Datei-Anhang wird komplett übertragen — nur die passenden Text-Fragmente", systemImage: "checkmark.shield")
                    }
                    .font(.caption)

                    VStack(alignment: .leading, spacing: 6) {
                        Text("Was NICHT gesendet wird")
                            .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                        Label("Deine anderen, nicht relevanten Dateien", systemImage: "xmark.circle")
                        Label("Passwörter, Zertifikate, Zugriffs-Tokens", systemImage: "lock.fill")
                    }
                    .font(.caption)
                    // v1.11.13: Apple-5.1.1(i) — die vorherige Zeile behauptete
                    // pauschal "Deine E-Mail, dein Name" werde nie gesendet.
                    // Diese Zustimmung (AiConsentedAt) gilt aber App-weit für
                    // dasselbe Konto, auch für Web-only-Features wie den
                    // KI-Einladungstext-Entwurf, der bewusst Name + Empfänger-
                    // Email in den Prompt schreibt — die Aussage war für den
                    // Account als Ganzes schlicht falsch. Jetzt nur noch
                    // Behauptungen, die für JEDES Feature stimmen.

                    // v1.10.165: Privacy-Link auf den aktuellen Server der
                    // Instanz — NimShare ist self-hosted, hardcoded nimshare.com
                    // war falsch für alle User anderer Instanzen.
                    if let base = auth.serverURL {
                        let privacyUrl = base.appendingPathComponent("privacy").absoluteString
                        Text("Du kannst diese Zustimmung jederzeit im Profil widerrufen. Details in der [Datenschutzerklärung](\(privacyUrl)).")
                            .font(.caption).foregroundStyle(.secondary)
                            .tint(Theme.cyan)
                    } else {
                        Text("Du kannst diese Zustimmung jederzeit im Profil widerrufen.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                .padding()
            }
            .navigationTitle("KI-Zustimmung")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Ablehnen") { decide(false) }.disabled(busy)
                }
                ToolbarItem(placement: .confirmationAction) {
                    // v1.10.165: Erlauben ist BLOCKIERT bis Provider-Info da ist —
                    // Apple 5.1.1(i) verlangt, dass der Empfänger benannt ist bevor
                    // der User zustimmt.
                    Button("Erlauben") { decide(true) }
                        .disabled(busy || auth.aiProviderInfo == nil)
                        .fontWeight(.semibold)
                }
            }
            .overlay { if busy { ProgressView() } }
            .task {
                // Provider-Info sicherstellen (könnte race-condition-mäßig
                // beim App-Start noch nicht geladen sein).
                if auth.aiProviderInfo == nil {
                    loadingProviderInfo = true
                    await auth.refreshAiConsent()
                    loadingProviderInfo = false
                }
            }
        }
    }

    private func decide(_ granted: Bool) {
        busy = true
        Task {
            await auth.setAiConsent(granted)
            busy = false
            onDecided(granted)
            dismiss()
        }
    }

    private func providerDisplayName(_ raw: String) -> String {
        switch raw {
        case "OpenAi": return "OpenAI"
        case "Anthropic": return "Anthropic"
        case "AzureOpenAi": return "Azure OpenAI (deine Region)"
        case "Gemini": return "Google Gemini"
        case "Disabled": return "keiner — KI ist auf dieser Instanz nicht aktiviert"
        default: return raw
        }
    }
}
