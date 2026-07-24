using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Full analytics page for a single share link — landings, downloads, password
/// failures over time, unique visitors, referrer/UA breakdown, geographic +
/// device + timezone splits, hour-of-day heatmap. Rendered as a server-side
/// page under /links/{id} so we don't need a client-side charting library;
/// small inline SVGs are enough.
/// </summary>
[Authorize(Policy = "WebUser")]
public class LinkReportController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly bool _storeFullIp;

    public LinkReportController(NimShareDbContext db, ICurrentUserService users, IConfiguration cfg)
    {
        _db = db;
        _users = users;
        _storeFullIp = cfg.GetValue<bool>("ShareLinks:StoreFullIp");
    }

    public record DailyBucket(DateOnly Day, int Landings, int Downloads, int PasswordFails);
    public record ReferrerRow(string Source, int Count);
    // v1.10.158: neue Aggregat-Records für Country/City/Device/Timezone-Karten.
    public record CountRow(string Key, int Count);
    // 7-Tage-Wochenraster × 24 Stunden (UTC). Werte = Landing-Anzahl.
    public record HourHeatmapCell(int DayOfWeek, int Hour, int Count);

    [HttpGet("/links/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks
            .Include(l => l.File)
            .Include(l => l.Folder)
            .SingleOrDefaultAsync(l => l.Id == id && (l.OwnerId == me.Id || me.Role == UserRole.Admin), ct);
        if (link is null) return NotFound();

        // v1.10.158: ALLE Events des Links laden für die Aggregate. Die Event-
        // Log-Tabelle in der View bleibt bei den letzten 500 (Reihenfolge:
        // .OrderByDescending → Take(500)); Aggregate werden über den vollen
        // Query gebaut. Für Links mit sehr vielen Aufrufen könnte das später
        // per SQL-Group-By ersetzt werden — für aktuelle Volumen reicht
        // In-Memory-Aggregation.
        var allEvents = await _db.ShareLinkAccesses
            .Where(a => a.ShareLinkId == id)
            .OrderByDescending(a => a.At)
            .ToListAsync(ct);
        var events = allEvents.Take(500).ToList();

        // 30-Tage-Rollup nur aus events (500) — für den Chart.
        var since = DateTimeOffset.UtcNow.Date.AddDays(-29);
        var byDay = new List<DailyBucket>();
        for (int d = 0; d < 30; d++)
        {
            var day = DateOnly.FromDateTime(since.AddDays(d));
            byDay.Add(new DailyBucket(day, 0, 0, 0));
        }
        foreach (var e in events.Where(e => e.At >= since))
        {
            var day = DateOnly.FromDateTime(e.At.UtcDateTime.Date);
            var idx = byDay.FindIndex(x => x.Day == day);
            if (idx < 0) continue;
            var b = byDay[idx];
            byDay[idx] = e.Kind switch
            {
                ShareLinkAccessKind.Landing => b with { Landings = b.Landings + 1 },
                ShareLinkAccessKind.Download => b with { Downloads = b.Downloads + 1 },
                ShareLinkAccessKind.PasswordFail => b with { PasswordFails = b.PasswordFails + 1 },
                _ => b,
            };
        }

        // Unique visitors ~ distinct IP hashes über ALLE Events.
        var uniqueVisitors = allEvents.Select(e => e.IpHash).Where(h => !string.IsNullOrEmpty(h)).Distinct().Count();

        // Referrer top list — collapse host only. Aus ALLEN Events.
        var refs = allEvents
            .Select(e => e.Referer ?? "")
            .Select(NormaliseReferer)
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!)
            .Select(g => new ReferrerRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(8)
            .ToList();

        // v1.10.158: Länder-Top-Liste (ISO-2, wird in der View mit Flag-Emoji
        // gepaart über den ISO→Regional-Indicator-Umbau).
        var countries = allEvents
            .Where(e => !string.IsNullOrEmpty(e.CountryCode))
            .GroupBy(e => e.CountryCode!.ToUpperInvariant())
            .Select(g => new CountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(10)
            .ToList();

        // Städte-Top-Liste — City-Werte kommen aus GeoIP nur wenn der
        // Provider auf Stadt-Ebene auflöst. „—" ausblenden.
        var cities = allEvents
            .Where(e => !string.IsNullOrEmpty(e.City))
            .GroupBy(e => e.City!)
            .Select(g => new CountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(10)
            .ToList();

        // Geräte-Split — Desktop/Mobile/Tablet/Bot.
        var devices = allEvents
            .Select(e => string.IsNullOrEmpty(e.DeviceType) || e.DeviceType == "Unknown" ? "Unbekannt" : e.DeviceType!)
            .GroupBy(d => d)
            .Select(g => new CountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .ToList();

        // Timezones — via /beacon nachträglich gestempelt; meist null bis
        // JS-Beacon lief.
        var timezones = allEvents
            .Where(e => !string.IsNullOrEmpty(e.Timezone))
            .GroupBy(e => e.Timezone!)
            .Select(g => new CountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(10)
            .ToList();

        // Hour-of-Day-Heatmap: 7 Wochentage (Sun=0…Sat=6) × 24 Stunden (UTC),
        // Wert = Landings + Downloads. Nur letzte 30 Tage — sonst mischen sich
        // saisonale Muster.
        var heatmap = new int[7, 24];
        foreach (var e in allEvents.Where(e => e.At >= since && e.Kind != ShareLinkAccessKind.PasswordFail))
        {
            var d = e.At.UtcDateTime;
            heatmap[(int)d.DayOfWeek, d.Hour]++;
        }
        var heatCells = new List<HourHeatmapCell>(7 * 24);
        for (int dow = 0; dow < 7; dow++)
            for (int h = 0; h < 24; h++)
                heatCells.Add(new HourHeatmapCell(dow, h, heatmap[dow, h]));

        // Time-to-Download-Median: für jeden IpHash die Zeit zwischen erstem
        // Landing und erstem Download berechnen. Nur für IpHashes mit BEIDEN.
        var byIp = allEvents.GroupBy(e => e.IpHash).Where(g => !string.IsNullOrEmpty(g.Key));
        var deltas = new List<TimeSpan>();
        foreach (var g in byIp)
        {
            var firstLanding = g.Where(e => e.Kind == ShareLinkAccessKind.Landing).OrderBy(e => e.At).FirstOrDefault();
            var firstDownload = g.Where(e => e.Kind == ShareLinkAccessKind.Download).OrderBy(e => e.At).FirstOrDefault();
            if (firstLanding is null || firstDownload is null) continue;
            if (firstDownload.At < firstLanding.At) continue;
            deltas.Add(firstDownload.At - firstLanding.At);
        }
        TimeSpan? medianTtd = null;
        if (deltas.Count > 0)
        {
            var sorted = deltas.OrderBy(t => t).ToList();
            medianTtd = sorted[sorted.Count / 2];
        }

        ViewData["Events"] = events;
        ViewData["AllEventCount"] = allEvents.Count;
        ViewData["Buckets"] = byDay;
        ViewData["UniqueVisitors"] = uniqueVisitors;
        ViewData["Referrers"] = refs;
        ViewData["Countries"] = countries;
        ViewData["Cities"] = cities;
        ViewData["Devices"] = devices;
        ViewData["Timezones"] = timezones;
        ViewData["HeatCells"] = heatCells;
        ViewData["MedianTtd"] = medianTtd;
        ViewData["StoreFullIp"] = _storeFullIp;
        return View(link);
    }

    private static string NormaliseReferer(string? r)
    {
        if (string.IsNullOrWhiteSpace(r)) return "";
        try
        {
            var u = new Uri(r);
            return u.Host;
        }
        catch { return "(direct)"; }
    }

    /// <summary>Wandelt einen ISO-3166-1-Alpha-2-Code (z.B. "DE") in das
    /// Flag-Emoji (🇩🇪) — jedes Zeichen wird auf sein Regional-Indicator-
    /// Codepoint verschoben. Ungültige Codes fallen still zurück.</summary>
    public static string CountryFlag(string? iso2)
    {
        if (string.IsNullOrEmpty(iso2) || iso2.Length != 2) return "🌐";
        var chars = iso2.ToUpperInvariant();
        if (chars[0] < 'A' || chars[0] > 'Z' || chars[1] < 'A' || chars[1] > 'Z') return "🌐";
        int a = char.ConvertToUtf32("🇦", 0);
        return char.ConvertFromUtf32(a + (chars[0] - 'A'))
             + char.ConvertFromUtf32(a + (chars[1] - 'A'));
    }
}
