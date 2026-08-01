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
            // v1.10.167: sobald der User Consent ERTEILT (nil/false → true),
            // die KI-Begrüssung nachladen. Sonst blieb der Fallback-Text für
            // die gesamte Session stehen, obwohl der Consent längst gegeben war.
            .onChange(of: auth.aiConsented) { _, new in
                if new == true {
                    Task {
                        message = nil; salutation = nil
                        await initialLoad()
                    }
                } else if new == false {
                    // v1.11.55: umgekehrter Fall fehlte — nach Widerruf (z.B.
                    // im Profil, während der Banner im Hintergrund weiterlebt)
                    // blieb die zuvor geladene KI-Begrüssung stehen und
                    // widersprach damit ProfileView's Zusage "Widerrufen
                    // deaktiviert diese Funktionen sofort".
                    Task {
                        message = nil; salutation = nil
                        await load(lat: nil, lon: nil)
                    }
                }
            }
    }

    @ViewBuilder
    private var content: some View {
        if let msg = message {
            // v1.11.67 (redesign-Pilot): größere, editorialere Begrüssung
            // (TFont.titleL statt .title3) + Wetter als eigenständiger Chip
            // rechts, matched den Home-Screen der neuen Design-Richtung.
            // Anrede-Logik/Datenfluss unverändert — nur die Präsentation.
            HStack(alignment: .top, spacing: Theme.Space.sm) {
                VStack(alignment: .leading, spacing: 2) {
                    if let s = salutation, !s.isEmpty {
                        // v1.11.71: war .lineLimit(1) — bei align-items:flex-
                        // end zwischen Name+Wetter-Chip wurde der Name mit
                        // Ellipsis abgeschnitten ("Guten Morgen, Marc…"),
                        // Marcus's Report "Name nicht ganz sichtbar, wirkt
                        // unhöflich". Jetzt 2 Zeilen statt Abschneiden, plus
                        // layoutPriority damit die Begrüssung Vorrang vor dem
                        // Wetter-Chip bekommt, falls beides eng wird.
                        Text("\(s) 👋")
                            .font(TFont.titleL)
                            .foregroundStyle(Theme.textPrimary)
                            .lineLimit(2)
                            .fixedSize(horizontal: false, vertical: true)
                            .layoutPriority(1)
                    }
                    Text(msg)
                        .font(TFont.bodyS)
                        .foregroundStyle(Theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: Theme.Space.s)
                // v1.10.140: Wetter oben rechts IN der Begrüssungs-Box (vorher
                // ein eigenes Nav-Bar-Symbol, das eine ganze Zeile Höhe kostete).
                if loading {
                    ProgressView().controlSize(.mini)
                } else if let w = weather {
                    RSWeatherChip(weather: w)
                }
            }
            .padding(.horizontal, Theme.Space.xl)
            .padding(.top, Theme.Space.s)
            .padding(.bottom, Theme.Space.xs)
            .contentShape(Rectangle())
            .onTapGesture { Task { await reload() } }
        } else {
            // Nullhöhen-Platzhalter: hält die Zeile in der Hierarchie (damit
            // `.task` läuft), ist aber unsichtbar bis die Begrüssung da ist.
            Color.clear.frame(height: 0)
        }
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
        // v1.10.165: KI-Begrüssung ist ein AI-Feature (Prompt basiert auf User-
        // Name + Uhrzeit + optional Ort). Ohne Consent den Fallback zeigen,
        // KEIN modales Sheet aufziehen — der Banner ist beim App-Start
        // sichtbar, ein sofortiges Modal wäre übergriffig. Der User erlebt
        // das Consent-Sheet beim ersten aktiv-getriggerten Feature (Chat,
        // Suche).
        if !auth.aiReady {
            // v1.10.167: statt karges „Hallo" jetzt die lokalisierte Tageszeit-
            // Anrede mit Namen + dezenter Consent-Hinweis. Tap auf die Box
            // öffnet nicht direkt das AI-Consent-Sheet (das würde beim App-
            // Start übergriffig wirken), sondern der Hinweis führt den User
            // ins Profil unter „KI-Nutzung". Aktive AI-Features (Chat, Suche)
            // triggern das Sheet weiterhin bei Bedarf.
            let f = localFallback()
            salutation = f.salutation
            message = auth.aiConsented == false
                ? "KI-Verarbeitung ist im Profil noch nicht aktiviert."
                : f.message
            return
        }
        loading = true; defer { loading = false }
        do {
            let g = try await api.greeting(lat: lat, lon: lon)
            salutation = g.salutation
            message = g.message
        }
        catch is CancellationError {
            // v1.10.142: Abbruch (View abgebaut / Task gecancelt) ist KEIN
            // echter Fehler. Vorher setzte der nackte catch hier den Offline-
            // Fallback → message != nil → die initialLoad-Guard blockierte
            // jeden weiteren Versuch, man hing die ganze Session auf der
            // generischen Begrüssung. Jetzt nichts setzen, damit ein späterer
            // Versuch die echte KI-Begrüssung noch lädt.
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
    // v1.10.172: Timeout-Task-Handle, damit ein alter Sleep den neuen Request
    // nicht vorzeitig auflöst (Race bei zweitem requestOnce nach schnellem
    // ersten Callback).
    private var timeoutTask: Task<Void, Never>?
    @Published var last: CLLocationCoordinate2D?

    override init() {
        super.init()
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyKilometer
    }

    func requestOnce() async -> CLLocationCoordinate2D? {
        let status = manager.authorizationStatus
        if status == .denied || status == .restricted { return nil }
        // v1.10.141: Läuft schon ein Request? Nicht doppelt starten — das würde
        // die vorige Continuation überschreiben und leaken. Letzten Fix zurück.
        if continuation != nil { return last }
        // v1.10.141: cancellation-sicher + Timeout-Sicherheitsnetz, damit die
        // Continuation IMMER genau einmal aufgelöst wird (behebt „leaked its
        // continuation without resuming it"): kommt kein Standort-Callback —
        // View verschwindet, requestLocation liefert nichts — löst spätestens
        // der Timeout auf.
        return await withTaskCancellationHandler {
            await withCheckedContinuation { cont in
                continuation = cont
                if status == .notDetermined {
                    manager.requestWhenInUseAuthorization()
                } else {
                    manager.requestLocation()
                }
                // v1.10.172: alten Timer verwerfen bevor wir einen neuen starten,
                // sonst löst der alte den JETZT laufenden Request auf.
                timeoutTask?.cancel()
                timeoutTask = Task { @MainActor in
                    try? await Task.sleep(nanoseconds: 8_000_000_000)
                    if !Task.isCancelled { finish(last, setLast: false) }
                }
            }
        } onCancel: {
            Task { @MainActor in self.finish(nil, setLast: false) }
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
