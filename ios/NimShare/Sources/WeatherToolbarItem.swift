import SwiftUI

/// v1.10.122 — Kleines Wetter-Symbol für die Nav-Bar (oben rechts auf der
/// „Dateien"-Startseite). Holt einmalig den Standort (teilt sich die Logik mit
/// der Begrüssung über OneShotLocation) und lädt vom Server ein kompaktes
/// Wetter-Objekt: SF-Symbol + aktuelle Temperatur, im Tap ein Popover mit
/// heutigem Hoch/Tief. Ohne Standort-Freigabe bleibt es unsichtbar — kein
/// Platzhalter, keine Fehlermeldung.
struct WeatherToolbarItem: View {
    @EnvironmentObject var auth: AuthStore
    @StateObject private var loc = OneShotLocation()
    @State private var info: NimShareAPI.WeatherInfo?
    @State private var showDetail = false

    var body: some View {
        Group {
            if let w = info {
                Button { showDetail = true } label: {
                    HStack(spacing: 4) {
                        Image(systemName: w.sfSymbol)
                            .symbolRenderingMode(.multicolor)
                        Text("\(w.tempC)°")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.primary)
                    }
                }
                .popover(isPresented: $showDetail) {
                    weatherDetail(w)
                        .presentationCompactAdaptation(.popover)
                }
            } else {
                Color.clear.frame(width: 0, height: 0)
            }
        }
        .task { await load() }
    }

    @ViewBuilder
    private func weatherDetail(_ w: NimShareAPI.WeatherInfo) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: w.sfSymbol)
                    .symbolRenderingMode(.multicolor)
                    .font(.title2)
                Text(w.text.prefix(1).uppercased() + w.text.dropFirst())
                    .font(.headline)
            }
            Divider()
            HStack(spacing: 16) {
                Label("\(w.highC)°", systemImage: "arrow.up")
                    .foregroundStyle(Theme.warnRed)
                Label("\(w.lowC)°", systemImage: "arrow.down")
                    .foregroundStyle(Theme.tungstenBlue)
            }
            .font(.subheadline)
            Text("Jetzt \(w.tempC)°")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(16)
        .frame(minWidth: 200)
    }

    private func load() async {
        guard let api = auth.api, info == nil else { return }
        // Standort ist Pflicht fürs Wetter — ohne Freigabe bleibt das Symbol weg.
        guard let c = await loc.requestOnce() else { return }
        do { info = try await api.weather(lat: c.latitude, lon: c.longitude) }
        catch { /* Wetter ist optional — kein Symbol statt Fehlermeldung */ }
    }
}
