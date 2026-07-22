import SwiftUI
import CoreLocation

/// v1.10.114 — KI-Begrüssung oben auf der Dateien-Startseite. Freundlich,
/// kontextbezogen (Tageszeit), und falls der Nutzer Standort freigibt auch
/// mit Wetter. Standort ist strikt optional: ohne Freigabe kommt trotzdem
/// eine Begrüssung, nur ohne Wetter. Ein Fingertipp lädt eine neue.
struct GreetingBanner: View {
    @EnvironmentObject var auth: AuthStore
    @State private var salutation: String?
    @State private var message: String?
    @State private var weather: NimShareAPI.WeatherInfo?
    @State private var loading = false
    @StateObject private var loc = OneShotLocation()

    var body: some View {
        // v1.10.120: WICHTIG — die View muss immer präsent sein, damit
        // `.task` zuverlässig feuert. Vorher war der Root eine leere Group
        // (text == nil → EmptyView), auf der `.task` nicht ausgelöst wurde
        // → die Begrüssung wurde nie geladen und tauchte nie auf.
        content
            .listRowInsets(EdgeInsets(top: 8, leading: 16, bottom: 4, trailing: 16))
            .listRowSeparator(.hidden)
            .listRowBackground(Color.clear)
            .task { await initialLoad() }
    }

    @ViewBuilder
    private var content: some View {
        if let msg = message {
            // v1.10.128: Ordentliche Anrede-Formatierung — Zeile 1 die Anrede
            // mit Namen (fett, ersetzt den weggefallenen „Dateien"-Titel),
            // darunter die Nachricht. Links ausgerichtet, echter Header-Look.
            HStack(alignment: .top, spacing: 10) {
                VStack(alignment: .leading, spacing: 3) {
                    if let s = salutation, !s.isEmpty {
                        Text("👋 \(s)")
                            .font(.title3.weight(.semibold))
                            .foregroundStyle(.primary)
                    }
                    Text(msg)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                // v1.10.140: Wetter oben rechts IN der Begrüssungs-Box (vorher
                // ein eigenes Nav-Bar-Symbol, das eine ganze Zeile Höhe kostete).
                if loading {
                    ProgressView().controlSize(.mini)
                } else if let w = weather {
                    weatherChip(w)
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
            .background(
                RoundedRectangle(cornerRadius: 14)
                    .fill(Theme.tungstenBlue.opacity(0.08))
            )
            .contentShape(Rectangle())
            .onTapGesture { Task { await reload() } }
        } else {
            // Nullhöhen-Platzhalter: hält die Zeile in der Hierarchie (damit
            // `.task` läuft), ist aber unsichtbar bis die Begrüssung da ist.
            Color.clear.frame(height: 0)
        }
    }

    /// v1.10.140: kompaktes Wetter oben rechts in der Begrüssungs-Box:
    /// Symbol + aktuelle Temperatur, darunter Hoch/Tief.
    @ViewBuilder
    private func weatherChip(_ w: NimShareAPI.WeatherInfo) -> some View {
        VStack(alignment: .trailing, spacing: 1) {
            HStack(spacing: 3) {
                Image(systemName: w.sfSymbol)
                    .symbolRenderingMode(.multicolor)
                Text("\(w.tempC)°")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.primary)
            }
            Text("↑\(w.highC)° ↓\(w.lowC)°")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .fixedSize()
    }

    private func initialLoad() async {
        guard message == nil, !loading else { return }
        // Zuerst schnell ohne Standort begrüssen, damit sofort etwas da ist.
        await load(lat: nil, lon: nil)
        // Dann — falls möglich — mit Standort fürs Wetter nachlegen.
        if let c = await loc.requestOnce() {
            await load(lat: c.latitude, lon: c.longitude)
        }
    }

    private func reload() async {
        let c = loc.last
        await load(lat: c?.latitude, lon: c?.longitude)
    }

    private func load(lat: Double?, lon: Double?) async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do {
            let g = try await api.greeting(lat: lat, lon: lon)
            salutation = g.salutation
            message = g.message
        }
        catch {
            // v1.10.120: Server hat den Greeting-Endpoint (noch) nicht oder
            // ein Netzfehler — lokalen Fallback zeigen statt gar nichts, aber
            // nur wenn noch keine (echte) Begrüssung geladen wurde.
            if message == nil {
                let f = localFallback()
                salutation = f.salutation
                message = f.message
            }
        }
        // v1.10.140: Wetter mitladen, sobald Standort da ist — Anzeige rechts
        // oben in der Box.
        if let la = lat, let lo = lon {
            weather = try? await api.weather(lat: la, lon: lo)
        }
    }

    private func localFallback() -> (salutation: String, message: String) {
        // v1.10.137: in der App-Sprache statt hart Deutsch (nur relevant, wenn
        // der Server offline ist — sonst kommt die Anrede lokalisiert vom Server).
        let lang = Bundle.main.preferredLocalizations.first ?? "de"
        let hour = Calendar.current.component(.hour, from: Date())
        let slot = hour < 11 ? 0 : hour < 18 ? 1 : 2
        let hi: String
        let msg: String
        switch lang {
        case "de": hi = [ "Guten Morgen", "Hallo", "Guten Abend" ][slot]; msg = "schön, dass du da bist."
        case "fr": hi = [ "Bonjour", "Bonjour", "Bonsoir" ][slot];        msg = "ravi de vous revoir."
        case "it": hi = [ "Buongiorno", "Ciao", "Buonasera" ][slot];      msg = "bello rivederti."
        case "es": hi = [ "Buenos días", "Hola", "Buenas tardes" ][slot]; msg = "qué bueno verte."
        case "nl": hi = [ "Goedemorgen", "Hallo", "Goedenavond" ][slot];  msg = "fijn dat je er bent."
        default:   hi = [ "Good morning", "Hello", "Good evening" ][slot]; msg = "nice to see you."
        }
        let name = auth.user?.displayName.split(separator: " ").first.map(String.init) ?? ""
        let sal = name.isEmpty ? "\(hi)," : "\(hi), \(name),"
        return (sal, msg)
    }
}

/// Minimaler Standort-Helfer: fragt „when in use", holt EINEN Fix und liefert
/// ihn zurück. Verweigert der Nutzer, gibt requestOnce() nil zurück.
@MainActor
final class OneShotLocation: NSObject, ObservableObject, CLLocationManagerDelegate {
    private let manager = CLLocationManager()
    private var continuation: CheckedContinuation<CLLocationCoordinate2D?, Never>?
    @Published var last: CLLocationCoordinate2D?

    override init() {
        super.init()
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyKilometer
    }

    func requestOnce() async -> CLLocationCoordinate2D? {
        let status = manager.authorizationStatus
        if status == .denied || status == .restricted { return nil }
        return await withCheckedContinuation { cont in
            continuation = cont
            if status == .notDetermined {
                manager.requestWhenInUseAuthorization()
            } else {
                manager.requestLocation()
            }
        }
    }

    // v1.10.119: Delegate-Callbacks kommen von CoreLocation auf einem
    // beliebigen Thread → nonisolated. Wir ziehen NUR Sendable-Werte
    // (Enum, Coordinate-Struct) raus und hüpfen für den State-Zugriff auf
    // den MainActor. Behebt die Swift-6-Data-Race-Warnung.
    nonisolated func locationManagerDidChangeAuthorization(_ m: CLLocationManager) {
        let status = m.authorizationStatus
        Task { @MainActor in
            switch status {
            case .authorizedWhenInUse, .authorizedAlways:
                self.manager.requestLocation()
            case .denied, .restricted:
                self.finish(nil, setLast: false)
            default:
                break
            }
        }
    }

    nonisolated func locationManager(_ m: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        let c = locations.last?.coordinate
        Task { @MainActor in self.finish(c, setLast: true) }
    }

    nonisolated func locationManager(_ m: CLLocationManager, didFailWithError error: Error) {
        Task { @MainActor in self.finish(nil, setLast: false) }
    }

    private func finish(_ c: CLLocationCoordinate2D?, setLast: Bool) {
        if setLast { last = c }
        continuation?.resume(returning: c)
        continuation = nil
    }
}
