using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NimShare.Api.Services;

/// <summary>
/// Short-lived in-memory store for the "password checked out, waiting on
/// TOTP code" step of the API login flow. Kept in-process because
/// challenges expire in minutes and losing them on a restart just forces
/// the client to log in again — no compliance cost.
/// </summary>
public interface ITotpChallengeStore
{
    string Create(Guid userId, TimeSpan ttl);
    Guid? Consume(string token);
}

public class TotpChallengeStore : ITotpChallengeStore
{
    private record Entry(Guid UserId, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public string Create(Guid userId, TimeSpan ttl)
    {
        var raw = RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        _entries[token] = new Entry(userId, DateTimeOffset.UtcNow.Add(ttl));
        // Piggy-back a cheap sweep on write; keeps the dict small.
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _entries)
            if (kv.Value.ExpiresAt <= now) _entries.TryRemove(kv.Key, out _);
        return token;
    }

    public Guid? Consume(string token)
    {
        if (!_entries.TryRemove(token, out var e)) return null;
        if (e.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        return e.UserId;
    }
}
