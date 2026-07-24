import SwiftUI

/// v1.10.158 — iOS-Parität zum Web-Link-Report (/links/{id}). Zeigt die
/// gleichen Aggregate: Kernzahlen, Country/City/Device/Timezone-Splits,
/// Peak-Hour-Heatmap (letzte 30 Tage), Referrer-Top-Liste und die letzten
/// 200 Zugriffs-Events (mit optionaler IP-Spalte wenn der Instanz-Betreiber
/// ShareLinks:StoreFullIp aktiviert hat).
struct LinkReportView: View {
    @EnvironmentObject var auth: AuthStore
    let linkId: UUID
    let slug: String

    @State private var report: NimShareAPI.LinkReportResponse?
    @State private var loading = true
    @State private var error: String?

    var body: some View {
        Group {
            if loading && report == nil {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error, report == nil {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                    Button("Erneut versuchen") { Task { await load() } }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let r = report {
                ScrollView {
                    VStack(alignment: .leading, spacing: 16) {
                        statTiles(r)
                        aggregateCard(title: "🌍 Länder", rows: r.countries, keyPrefix: { Self.countryFlag($0) })
                        aggregateCard(title: "🏙 Städte", rows: r.cities)
                        aggregateCard(title: "📱 Geräte", rows: r.devices, keyPrefix: { deviceIcon($0) })
                        aggregateCard(title: "🕒 Zeitzonen", rows: r.timezones, monospace: true)
                        aggregateCard(title: "🔗 Herkunft", rows: r.referrers, monospace: true)
                        heatmapCard(r.hourHeatmap)
                        eventsCard(r)
                    }
                    .padding(16)
                }
            }
        }
        .navigationTitle("📊 \(slug)")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .refreshable { await load() }
    }

    // MARK: - Sub-Views

    @ViewBuilder
    private func statTiles(_ r: NimShareAPI.LinkReportResponse) -> some View {
        let tiles: [(label: String, value: String)] = [
            ("Aufrufe", "\(r.hitCount)"),
            ("Downloads", "\(r.downloadCount)"),
            ("Einzigartig", "\(r.uniqueVisitors)"),
            ("Ø Zeit → DL", r.medianTimeToDownloadSeconds.map(Self.formatSeconds) ?? "—"),
        ]
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 12)], spacing: 12) {
            ForEach(tiles, id: \.label) { t in
                VStack(alignment: .leading, spacing: 4) {
                    Text(t.label).font(.caption).foregroundStyle(.secondary)
                    Text(t.value).font(.title2.weight(.bold)).foregroundStyle(Theme.tungstenBlue)
                }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 10).fill(Theme.cardBackground))
            }
        }
    }

    @ViewBuilder
    private func aggregateCard(title: String,
                               rows: [NimShareAPI.LinkReportCountRow],
                               keyPrefix: ((String) -> String)? = nil,
                               monospace: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.headline)
            if rows.isEmpty {
                Text("Keine Daten.").font(.caption).foregroundStyle(.secondary)
            } else {
                let total = rows.map(\.count).reduce(0, +)
                ForEach(rows, id: \.key) { r in
                    let pct = total == 0 ? 0 : Double(r.count) / Double(total)
                    HStack(spacing: 8) {
                        if let prefix = keyPrefix { Text(prefix(r.key)).font(.body) }
                        Text(r.key)
                            .font(monospace ? .caption.monospaced() : .caption)
                            .frame(width: 90, alignment: .leading)
                            .lineLimit(1)
                        GeometryReader { geo in
                            ZStack(alignment: .leading) {
                                RoundedRectangle(cornerRadius: 3).fill(Color.gray.opacity(0.15))
                                RoundedRectangle(cornerRadius: 3).fill(Theme.tungstenBlue)
                                    .frame(width: geo.size.width * CGFloat(pct))
                            }
                        }
                        .frame(height: 8)
                        Text("\(r.count)").font(.caption).foregroundStyle(.secondary).frame(width: 40, alignment: .trailing)
                    }
                }
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(Theme.cardBackground))
    }

    @ViewBuilder
    private func heatmapCard(_ cells: [NimShareAPI.LinkReportHeatCell]) -> some View {
        let dayLabels = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"]
        let maxCount = max(1, cells.map(\.count).max() ?? 1)
        VStack(alignment: .leading, spacing: 6) {
            Text("🔥 Peak-Zeiten · letzte 30 Tage (UTC)").font(.headline)
            ScrollView(.horizontal, showsIndicators: false) {
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 3) {
                        Text("").frame(width: 22)
                        ForEach(0..<24, id: \.self) { h in
                            Text("\(h)").font(.system(size: 8)).foregroundStyle(.secondary)
                                .frame(width: 14, alignment: .center)
                        }
                    }
                    ForEach(0..<7, id: \.self) { dow in
                        HStack(spacing: 3) {
                            Text(dayLabels[dow]).font(.system(size: 10, weight: .semibold))
                                .foregroundStyle(.secondary)
                                .frame(width: 22, alignment: .trailing)
                            ForEach(0..<24, id: \.self) { h in
                                let count = cells.first { $0.dayOfWeek == dow && $0.hour == h }?.count ?? 0
                                let intensity = Double(count) / Double(maxCount)
                                RoundedRectangle(cornerRadius: 2)
                                    .fill(Color.green.opacity(0.08 + 0.92 * intensity))
                                    .frame(width: 14, height: 14)
                            }
                        }
                    }
                }
            }
            Text("Intensität 0–\(maxCount) Zugriffe · Landings + Downloads")
                .font(.caption2).foregroundStyle(.secondary)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(Theme.cardBackground))
    }

    @ViewBuilder
    private func eventsCard(_ r: NimShareAPI.LinkReportResponse) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Zugriffs-Log · \(r.recentEvents.count) von \(r.totalEventCount)").font(.headline)
            if r.recentEvents.isEmpty {
                Text("Noch keine Zugriffe.").font(.caption).foregroundStyle(.secondary)
            } else {
                ForEach(Array(r.recentEvents.prefix(60).enumerated()), id: \.offset) { _, e in
                    HStack(spacing: 8) {
                        Text(kindIcon(e.kind)).font(.body)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(e.at.formatted(date: .abbreviated, time: .shortened)).font(.caption)
                            HStack(spacing: 6) {
                                if let loc = formatLocation(city: e.city, country: e.countryCode) {
                                    Text(loc).font(.caption2).foregroundStyle(.secondary)
                                }
                                if let d = e.deviceType, d != "Unknown", !d.isEmpty {
                                    Text(deviceIcon(d)).font(.caption2)
                                }
                                if r.storeFullIp, let ip = e.ipAddress {
                                    Text(ip).font(.caption2.monospaced()).foregroundStyle(.secondary)
                                }
                            }
                        }
                        Spacer()
                    }
                    .padding(.vertical, 2)
                    Divider()
                }
                if r.storeFullIp {
                    Text("IP-Speicherung ist Instanz-weit aktiviert (Art. 6(1)(f) DSGVO).")
                        .font(.caption2).foregroundStyle(.secondary).padding(.top, 4)
                }
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(Theme.cardBackground))
    }

    // MARK: - Helpers

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do { report = try await api.linkReport(id: linkId) }
        catch let ex { error = ex.localizedDescription }
    }

    private func kindIcon(_ kind: String) -> String {
        switch kind {
        case "Download": return "⬇️"
        case "PasswordFail": return "🔒"
        default: return "👁"
        }
    }

    private func deviceIcon(_ d: String) -> String {
        switch d {
        case "Desktop": return "🖥"
        case "Mobile": return "📱"
        case "Tablet": return "📲"
        case "Bot": return "🤖"
        default: return "❔"
        }
    }

    private func formatLocation(city: String?, country: String?) -> String? {
        if let c = city, !c.isEmpty {
            if let co = country, !co.isEmpty { return "\(c), \(co)" }
            return c
        }
        return country
    }

    /// ISO-3166-1-Alpha-2 → Flag-Emoji (jedes Buchstabe auf sein Regional-
    /// Indicator-Codepoint verschoben). Fallback für ungültige Codes.
    private static func countryFlag(_ iso2: String) -> String {
        guard iso2.count == 2 else { return "🌐" }
        let up = iso2.uppercased()
        var out = ""
        for ch in up.unicodeScalars {
            guard let scalar = ch.value >= 65 && ch.value <= 90
                ? Unicode.Scalar(0x1F1E6 + Int(ch.value - 65)) : nil else { return "🌐" }
            out.append(Character(scalar))
        }
        return out
    }

    private static func formatSeconds(_ s: Double) -> String {
        if s < 60 { return "\(Int(s)) s" }
        if s < 3600 { return "\(Int(s/60)) min" }
        if s < 86400 { return String(format: "%.1f h", s/3600) }
        return String(format: "%.1f d", s/86400)
    }
}
