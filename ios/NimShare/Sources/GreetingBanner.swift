import SwiftUI
import CoreLocation

/// v1.10.114 — KI-Begrüssung oben auf der Dateien-Startseite. Freundlich,
/// kontextbezogen (Tageszeit), und falls der Nutzer Standort freigibt auch
/// mit Wetter. Standort ist strikt optional: ohne Freigabe kommt trotzdem
/// eine Begrüssung, nur ohne Wetter. Ein Fingertipp lädt eine neue.
struct GreetingBanner: View {
    @EnvironmentObject var auth: AuthStore
    @State private var text: String?
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
        if let t = text {
            // v1.10.125: Ausgewogene Grösse — nicht so gross dass sie die
            // Kacheln verdrängt (v1.10.123), aber auch nicht so winzig dass
            // sie verloren wirkt (v1.10.124). subheadline, primäre Farbe,
            // vollständiger Text (kein hartes Abschneiden). Das adaptive
            // Raster gleicht die natürliche Höhe automatisch aus.
            HStack(alignment: .top, spacing: 10) {
                Text("👋").font(.title3)
                Text(t)
                    .font(.subheadline)
                    .foregroundStyle(.primary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                if loading { ProgressView().controlSize(.mini) }
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

    private func initialLoad() async {
        guard text == nil, !loading else { return }
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
        do { text = try await api.greeting(lat: lat, lon: lon) }
        catch {
            // v1.10.120: Server hat den Greeting-Endpoint (noch) nicht oder
            // ein Netzfehler — lokalen Fallback zeigen statt gar nichts, aber
            // nur wenn noch keine (echte) Begrüssung geladen wurde.
            if text == nil { text = localFallback() }
        }
    }

    private func localFallback() -> String {
        let hour = Calendar.current.component(.hour, from: Date())
        let hi = hour < 11 ? "Guten Morgen" : hour < 18 ? "Hallo" : "Guten Abend"
        let name = auth.user?.displayName.split(separator: " ").first.map(String.init) ?? ""
        return name.isEmpty ? "\(hi)! Schön, dass du da bist." : "\(hi), \(name)! Schön, dass du da bist."
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
