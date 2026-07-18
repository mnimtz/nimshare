using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;

namespace NimShare.Api.Services;

public interface ISlugService
{
    string GenerateRandom(int lengthChars = 10);
    bool IsValid(string slug);
    Task<bool> IsAvailableAsync(string slug, CancellationToken ct = default);
    Task<string> ResolveOrGenerateAsync(string? requested, CancellationToken ct = default);
}

public class SlugService : ISlugService
{
    // 3-64 chars, lowercase alphanum, `-` and `_` inside (not at ends).
    private static readonly Regex SlugPattern =
        new(@"^[a-z0-9](?:[a-z0-9_-]{1,62}[a-z0-9])?$", RegexOptions.Compiled);

    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // dropped I/l/O/0/1 for readability

    private readonly NimShareDbContext _db;

    public SlugService(NimShareDbContext db) => _db = db;

    public bool IsValid(string slug) =>
        !string.IsNullOrWhiteSpace(slug) && SlugPattern.IsMatch(slug);

    public string GenerateRandom(int lengthChars = 10)
    {
        Span<byte> bytes = stackalloc byte[lengthChars];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(lengthChars, bytes.ToArray(), (span, buf) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = Alphabet[buf[i] % Alphabet.Length];
        });
    }

    public async Task<bool> IsAvailableAsync(string slug, CancellationToken ct = default)
    {
        var taken = await _db.ShareLinks.AnyAsync(x => x.Slug == slug, ct)
                    || await _db.UploadRequests.AnyAsync(x => x.Slug == slug, ct);
        return !taken;
    }

    public async Task<string> ResolveOrGenerateAsync(string? requested, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!IsValid(requested))
                throw new ArgumentException($"Slug '{requested}' is not valid.", nameof(requested));
            if (!await IsAvailableAsync(requested, ct))
                throw new InvalidOperationException($"Slug '{requested}' is already taken.");
            return requested;
        }

        // Retry a handful of times if we happen to collide with an existing random slug.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var candidate = GenerateRandom(attempt < 3 ? 10 : 14);
            if (await IsAvailableAsync(candidate, ct))
                return candidate;
        }
        throw new InvalidOperationException("Could not generate a unique slug after 6 attempts.");
    }
}
