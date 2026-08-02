import SwiftUI

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

    @State private var mode: Mode = .search

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
            case .search: SearchView()
            case .chat: ChatView()
            }
        }
        .safeAreaInset(edge: .top) {
            Picker("Modus", selection: $mode) {
                ForEach(Mode.allCases) { m in
                    Text(m.label).tag(m)
                }
            }
            .pickerStyle(.segmented)
            .padding(.horizontal)
            .padding(.top, 8)
            .padding(.bottom, 4)
            .background(.bar)
        }
        .navigationTitle("KI")
        .navigationBarTitleDisplayMode(.inline)
    }
}
