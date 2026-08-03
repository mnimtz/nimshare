import SwiftUI

/// v2.0.2: Bereichs-Filter für Chat + Suche. Web hat seit v1.10.112 ein
/// <select> mit Default "Öffentlich" — iOS hatte so etwas noch nie (weder
/// Chat noch Suche haben je einen Scope mitgeschickt, seit dem allerersten
/// iOS-Commit). Der Server durchsucht bei leerem Scope zwar schon alles
/// Erreichbare (Personal+Public+DirectShares — eher zu viel als zu wenig),
/// aber ohne sichtbare Auswahl wirkte das für Marcus wie "findet nur
/// Privates".
///
/// v2.0.3: Gruppen-Option wieder entfernt — Marcus's Korrektur: "wieso gibt
/// es 3 Bereiche? sollte doch nur privat und öffentlich geben, Gruppen sind
/// ja keine Bereiche". Deckt sich mit der v1.10.102-Entscheidung, dass
/// Gruppen nur noch Verteiler-Namen für "Teilen mit → Gruppe" sind, keine
/// eigene durchsuchbare Bibliothek mehr.
enum KiScope: Hashable {
    case personal
    case `public`

    var apiValue: String {
        switch self {
        case .personal: return "Personal"
        case .public: return "Public"
        }
    }
}

/// v1.11.73 — Tab-Konsolidierung: "Suche" und "Chat" waren zwei separate
/// Tabs, obwohl beide dieselbe KI-Funktion (AI-Gateway, gleicher Consent-
/// Gate) nutzen. Marcus's Wunsch: zu einem "KI"-Tab zusammenlegen, per
/// Segment umschaltbar. SearchView/ChatView selbst bleiben unverändert in
/// ihrer Logik — nur ihr eigener .navigationTitle wurde entfernt, da der
/// jetzt hier zentral sitzt.
struct KIView: View {
    enum Mode: String, CaseIterable, Identifiable {
        case search, chat
        var id: Self { self }
        var label: LocalizedStringKey {
            switch self {
            case .search: return "Suche"
            case .chat: return "Chat"
            }
        }
    }

    @EnvironmentObject var auth: AuthStore
    @State private var mode: Mode = .search
    // v2.0.2: Default "Öffentlich" — spiegelt Web (v1.10.112: "dort liegt
    // die geteilte Dokumentation, über die man typischerweise chattet").
    @State private var scope: KiScope = .public

    var body: some View {
        // v2.0.1: Picker war ein VStack-Geschwister VOR ChatView/SearchView —
        // dadurch war ChatView nicht mehr der direkte Inhalt des
        // NavigationStack, und dessen .safeAreaInset(edge: .bottom)-Keyboard-
        // Fix (ChatView.swift) griff nicht mehr: die Tastatur überdeckte das
        // Eingabefeld komplett, nur der "Fertig"-Button (System-Keyboard-
        // Toolbar, von der Verschachtelung unabhängig) blieb sichtbar.
        // .safeAreaInset(edge: .top) statt VStack-Partitionierung komponiert
        // die Safe-Area korrekt durch, ChatView bleibt für Keyboard-Avoidance
        // "top-level" genug.
        Group {
            switch mode {
            case .search: SearchView(scope: $scope)
            case .chat: ChatView(scope: $scope)
            }
        }
        .safeAreaInset(edge: .top) {
            VStack(spacing: 6) {
                Picker("Modus", selection: $mode) {
                    ForEach(Mode.allCases) { m in
                        Text(m.label).tag(m)
                    }
                }
                .pickerStyle(.segmented)

                HStack(spacing: 6) {
                    Image(systemName: "target")
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.textTertiary)
                    Picker("Bereich", selection: $scope) {
                        Text("Öffentlich").tag(KiScope.public)
                        Text("Persönlich").tag(KiScope.personal)
                    }
                    .pickerStyle(.menu)
                    .font(TFont.bodyS)
                    .tint(Theme.textSecondary)
                    Spacer()
                }
            }
            .padding(.horizontal)
            .padding(.top, 8)
            .padding(.bottom, 4)
            .background(.bar)
        }
        .navigationTitle("KI")
        .navigationBarTitleDisplayMode(.inline)
    }
}
