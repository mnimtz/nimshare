using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Full analytics page for a single share link — landings, downloads, password
/// failures over time, unique visitors, referrer/UA breakdown. Rendered as a
/// server-side page under /links/{id} so we don't need a client-side charting
/// dependency; a tiny inline SVG bar chart is enough.
/// </summary>
[Authorize(Policy = "WebUser")]
public class LinkReportController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public LinkReportController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db;
        _users = users;
    }

    public record DailyBucket(DateOnly Day, int Landings, int Downloads, int PasswordFails);
    public record ReferrerRow(string Source, int Count);

    [HttpGet("/links/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks
            .Include(l => l.File)
            .Include(l => l.Folder)
            .SingleOrDefaultAsync(l => l.Id == id && (l.OwnerId == me.Id || me.Role == UserRole.Admin), ct);
        if (link is null) return NotFound();

        var events = await _db.ShareLinkAccesses
            .Where(a => a.ShareLinkId == id)
            .OrderByDescending(a => a.At)
            .Take(500)
            .ToListAsync(ct);

        // 30-day daily rollup for the chart. Bucketing done in memory —
        // Sqlite/SqlServer date arithmetic diverges enough to make it not
        // worth pushing down for this volume (500 rows max).
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

        // Unique visitors ~ distinct IP hashes.
        var uniqueVisitors = events.Select(e => e.IpHash).Where(h => !string.IsNullOrEmpty(h)).Distinct().Count();

        // Referrer top list — collapse host only.
        var refs = events
            .Select(e => e.Referer ?? "")
            .Select(NormaliseReferer)
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!)
            .Select(g => new ReferrerRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(8)
            .ToList();

        ViewData["Events"] = events;
        ViewData["Buckets"] = byDay;
        ViewData["UniqueVisitors"] = uniqueVisitors;
        ViewData["Referrers"] = refs;
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
}
