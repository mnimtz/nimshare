using System.Globalization;

namespace NimShare.Api.Services;

/// <summary>
/// Central place to resolve which timezone the app displays timestamps in.
/// Priority (highest wins): explicit config <c>NimShare:DisplayTimeZone</c>
/// → Azure App Service's <c>WEBSITE_TIME_ZONE</c> → current OS timezone.
/// Fallback if the resolved id isn't recognised is UTC + a startup warning.
///
/// This is a single-tenant setting for now — a per-user TZ preference would
/// require plumbing IUserContext into every timestamp render, which we can
/// do later if Marcus needs multi-region users. Today the app is used from
/// one company (Tungsten Automation), one timezone (Europe/Berlin).
/// </summary>
public interface ITimeService
{
    TimeZoneInfo DisplayZone { get; }
    string ZoneId { get; }

    /// <summary>Return the given UTC-anchored timestamp as a local
    /// wallclock string in the display zone (yyyy-MM-dd HH:mm).</summary>
    string Format(DateTimeOffset utc);
    string Format(DateTimeOffset? utc);

    /// <summary>ISO 8601 UTC — for &lt;time datetime="..."&gt; attributes
    /// (some views want JS-driven client-side reformat).</summary>
    string ToIsoUtc(DateTimeOffset utc);
}

public class TimeService : ITimeService
{
    public TimeZoneInfo DisplayZone { get; }
    public string ZoneId { get; }

    public TimeService(IConfiguration config, ILogger<TimeService> log)
    {
        var configured = config["NimShare:DisplayTimeZone"];
        var azureTz = Environment.GetEnvironmentVariable("WEBSITE_TIME_ZONE");
        var candidate = !string.IsNullOrWhiteSpace(configured) ? configured
            : !string.IsNullOrWhiteSpace(azureTz) ? azureTz
            : "Europe/Berlin"; // sensible EFIGS+NL default for the current user base
        try
        {
            DisplayZone = TimeZoneInfo.FindSystemTimeZoneById(candidate);
            ZoneId = candidate;
        }
        catch (TimeZoneNotFoundException)
        {
            log.LogWarning(
                "TimeZone '{Candidate}' not resolvable on this OS. Falling back to UTC. On Azure App Service set app-setting NimShare__DisplayTimeZone=Europe/Berlin.",
                candidate);
            DisplayZone = TimeZoneInfo.Utc;
            ZoneId = "UTC";
        }
        log.LogInformation("TimeService display zone = {ZoneId} (Offset={Offset}).",
            ZoneId, DisplayZone.GetUtcOffset(DateTimeOffset.UtcNow));
    }

    public string Format(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, DisplayZone)
                       .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string Format(DateTimeOffset? utc) => utc is null ? "—" : Format(utc.Value);

    public string ToIsoUtc(DateTimeOffset utc)
        => utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>Extension helpers for Razor views. Zero DI plumbing needed —
/// the singleton is resolved via a static holder set at startup.</summary>
public static class TimeDisplay
{
    private static ITimeService? _svc;
    public static void Register(ITimeService svc) => _svc = svc;

    /// <summary>Convert an internal UTC timestamp to the app's display zone.
    /// Returns the raw UTC ISO string if the service isn't wired up yet
    /// (safe for design-time renders).</summary>
    public static string ToDisplay(this DateTimeOffset utc)
        => _svc?.Format(utc) ?? utc.ToString("yyyy-MM-dd HH:mm 'UTC'");

    public static string ToDisplay(this DateTimeOffset? utc)
        => _svc?.Format(utc) ?? (utc?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "—");
}
