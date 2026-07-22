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
        Group {
            if let t = text {
                HStack(alignment: .top, spacing: 10) {
                    Text("👋").font(.title3)
                    Text(t)
                        .font(.callout)
                        .foregroundStyle(.primary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                    if loading { ProgressView().controlSize(.mini) }
                }
                .padding(12)
                .background(
                    RoundedRectangle(cornerRadius: 14)
                        .fill(Theme.tungstenBlue.opacity(0.08))
                )
                .contentShape(Rectangle())
                .onTapGesture { Task { await reload() } }
                .listRowInsets(EdgeInsets(top: 8, leading: 16, bottom: 4, trailing: 16))
                .listRowSeparator(.hidden)
                .listRowBackground(Color.clear)
            }
        }
        .task { await initialLoad() }
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
        catch { /* Begrüssung ist Beiwerk — Fehler still schlucken */ }
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

    func locationManagerDidChangeAuthorization(_ m: CLLocationManager) {
        switch m.authorizationStatus {
        case .authorizedWhenInUse, .authorizedAlways:
            m.requestLocation()
        case .denied, .restricted:
            finish(nil)
        default:
            break
        }
    }

    func locationManager(_ m: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        let c = locations.last?.coordinate
        last = c
        finish(c)
    }

    func locationManager(_ m: CLLocationManager, didFailWithError error: Error) {
        finish(nil)
    }

    private func finish(_ c: CLLocationCoordinate2D?) {
        continuation?.resume(returning: c)
        continuation = nil
    }
}
