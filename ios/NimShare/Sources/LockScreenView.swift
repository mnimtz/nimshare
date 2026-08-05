import SwiftUI

/// v2.0.5: Vollflächiges Sperr-Overlay, das vor der signedIn-Oberfläche steht,
/// wenn das biometrische App-Schloss aktiv ist (Variante A). Triggert die
/// Biometrie automatisch beim Erscheinen; bei Fehlschlag Retry + Abmelden.
struct LockScreenView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.scenePhase) private var scenePhase
    @State private var failed = false
    @State private var busy = false
    // v2.0.6: „scharf" für genau EINEN Auto-Versuch pro Vordergrund-Rückkehr.
    // Verhindert, dass der System-Face-ID-Dialog (der die App kurz
    // .inactive→.active schaukelt) einen Endlos-Prompt auslöst.
    @State private var armed = true

    private let bio = BiometricAuth.available

    var body: some View {
        ZStack {
            Theme.bgGradient.ignoresSafeArea()
            VStack(spacing: Theme.Space.xl) {
                Spacer()
                Image(systemName: bio == .none ? "lock.fill" : bio.systemImage)
                    .font(.system(size: 64, weight: .semibold))
                    .foregroundStyle(Theme.navyFg)
                VStack(spacing: Theme.Space.s) {
                    Text("NimShare ist gesperrt")
                        .font(TFont.titleL)
                        .foregroundStyle(Theme.textPrimary)
                    Text(failed ? "Entsperren fehlgeschlagen. Bitte erneut versuchen."
                                : "Zum Fortfahren entsperren")
                        .font(TFont.bodyM)
                        .foregroundStyle(Theme.textSecondary)
                        .multilineTextAlignment(.center)
                }
                Spacer()
                Button { Task { await attempt() } } label: {
                    Label(failed ? "Erneut versuchen" : "Entsperren",
                          systemImage: bio == .none ? "lock.open" : bio.systemImage)
                        .font(TFont.titleS)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 14)
                        .foregroundStyle(.white)
                        .background(RoundedRectangle(cornerRadius: Theme.Radius2.card).fill(Theme.navy))
                }
                .disabled(busy)
                .padding(.horizontal, Theme.Space.xxl)

                Button(role: .destructive) { auth.signOut() } label: {
                    Text("Abmelden").font(TFont.bodyM)
                }
                .padding(.bottom, Theme.Space.xxl)
            }
            .padding(.horizontal, Theme.Space.lg)
        }
        // v2.0.6: Face ID NICHT beim bloßen Erscheinen auslösen — der
        // Sperr-Screen erscheint schon beim Wechsel IN den Hintergrund, wo
        // Biometrie nicht laufen kann (deshalb musste man vorher beim
        // Zurückkommen manuell tippen). Stattdessen beim Wechsel in den
        // VORDERGRUND (.active) genau einmal automatisch versuchen.
        .task { triggerAutoUnlock() }
        .onChange(of: scenePhase) { _, phase in
            if phase == .background {
                armed = true          // für die nächste Rückkehr neu schärfen
                failed = false
            } else if phase == .active {
                triggerAutoUnlock()
            }
        }
    }

    /// Löst genau EINEN automatischen Biometrie-Versuch aus, sofern die App im
    /// Vordergrund und noch gesperrt ist. Manuelles Antippen umgeht das (Retry).
    @MainActor
    private func triggerAutoUnlock() {
        guard armed, scenePhase == .active, auth.isLocked else { return }
        armed = false
        Task { await attempt() }
    }

    @MainActor
    private func attempt() async {
        // Nur im Vordergrund — Biometrie im Hintergrund/inaktiv schlägt fehl.
        guard !busy, scenePhase == .active else { return }
        busy = true
        let ok = await auth.unlock()
        busy = false
        failed = !ok
    }
}

/// v2.0.5: Zeile in ProfileView → „Sicherheit". Schaltet das App-Schloss.
/// Aktivieren erst nach erfolgreicher Biometrie (kein Selbst-Aussperren);
/// ist keine Biometrie eingerichtet, wird eine deaktivierte Info-Zeile gezeigt.
struct BiometricLockRow: View {
    @EnvironmentObject var auth: AuthStore
    @State private var isOn = false
    @State private var busy = false

    private let bio = BiometricAuth.available

    var body: some View {
        Group {
            if bio == .none {
                Label("Biometrie nicht eingerichtet", systemImage: "faceid")
                    .foregroundStyle(Theme.textTertiary)
            } else {
                Toggle(isOn: Binding(get: { isOn }, set: { setEnabled($0) })) {
                    Label(bio.brand, systemImage: bio.systemImage)
                        .foregroundStyle(Theme.navyFg)
                }
                .tint(Theme.cyan)
                .disabled(busy)
            }
        }
        .onAppear { isOn = auth.biometricLockEnabled }
    }

    private func setEnabled(_ newValue: Bool) {
        guard newValue else {
            auth.biometricLockEnabled = false
            isOn = false
            return
        }
        // Vor dem Aktivieren einmal biometrisch bestätigen.
        busy = true
        Task {
            let ok = await BiometricAuth.authenticate(reason: String(localized: "NimShare entsperren"))
            await MainActor.run {
                busy = false
                auth.biometricLockEnabled = ok
                isOn = ok
            }
        }
    }
}
