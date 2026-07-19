using System.Collections.Concurrent;

namespace NimShare.Api.Services;

/// <summary>
/// In-process ring buffer of the last 40 email-delivery attempts. Rendered
/// on /diagnostics so the admin can see WHY the last Signatur-Invite or
/// upload-notification didn't arrive, without needing Azure Log Stream.
/// </summary>
public static class EmailDeliveryLog
{
    private const int Capacity = 40;
    private static readonly ConcurrentQueue<Entry> _entries = new();

    public record Entry(DateTimeOffset At, string To, string Subject, bool Ok, string? Error, string Kind);

    public static void Record(string to, string subject, bool ok, string? error, string kind)
    {
        _entries.Enqueue(new Entry(DateTimeOffset.UtcNow, to, subject, ok, error, kind));
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
    }

    public static IReadOnlyCollection<Entry> Snapshot() => _entries.Reverse().ToArray();
}
