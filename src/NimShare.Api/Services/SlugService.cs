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
    // v1.10.41: für den Live-Check im Share-Dialog. Bei belegtem Slug
    // liefert der Endpoint bis zu N freie Alternativen basierend auf dem
    // Wunsch — als konkrete Klick-Angebote statt "denk dir was Neues aus".
    Task<List<string>> SuggestAlternativesAsync(string requested, int count = 3, CancellationToken ct = default);
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

    /// <summary>
    /// Normalise a user-supplied slug candidate so obvious "wrong shape" input
    /// (mixed case, spaces, dots, umlauts) becomes a valid slug automatically
    /// instead of a 422. Callers pass this through <see cref="ResolveOrGenerateAsync"/>.
    /// </summary>
    public static string Normalise(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = input.Trim().ToLowerInvariant();
        // Cheap transliteration for the common Western-European letters we care
        // about; anything else falls through to the regex strip below.
        s = s.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        // Swap any run of non-[a-z0-9] for a single "-".
        s = Regex.Replace(s, "[^a-z0-9]+", "-");
        s = s.Trim('-', '_');
        if (s.Length > 64) s = s.Substring(0, 64).TrimEnd('-', '_');
        return s;
    }

    public async Task<string> ResolveOrGenerateAsync(string? requested, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            // Try the raw value first (preserves an already-valid slug); on
            // failure, normalise once and try again — that catches "PowerPDF",
            // "My Folder", "Rechnung 2025.03" etc. and turns them into
            // "powerpdf", "my-folder", "rechnung-2025-03".
            if (!IsValid(requested))
            {
                var normalised = Normalise(requested);
                if (!IsValid(normalised))
                    throw new ArgumentException($"Slug '{requested}' is not valid.", nameof(requested));
                requested = normalised;
            }
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

    public async Task<List<string>> SuggestAlternativesAsync(string requested, int count = 3, CancellationToken ct = default)
    {
        // Zuerst normalisieren — dann arbeiten wir auf einem konsistenten Base.
        var baseSlug = IsValid(requested) ? requested : Normalise(requested);
        if (string.IsNullOrEmpty(baseSlug)) return new List<string>();
        var out_ = new List<string>(count);
        // 1) Wenn der Slug bereits auf "-N" endet, um eins hochzählen. Sonst
        //    startet die Iteration bei -2 (menschlich intuitiver als -1).
        //    Bis zu 20 Versuchen — mehr wird lächerlich.
        var m = Regex.Match(baseSlug, @"^(?<stem>.+)-(?<n>\d+)$");
        string stem;
        int start;
        if (m.Success && int.TryParse(m.Groups["n"].Value, out var n))
        {
            stem = m.Groups["stem"].Value;
            start = n + 1;
        }
        else
        {
            stem = baseSlug;
            start = 2;
        }
        for (int i = start; i < start + 20 && out_.Count < count; i++)
        {
            var candidate = $"{stem}-{i}";
            if (!IsValid(candidate)) continue;
            if (await IsAvailableAsync(candidate, ct)) out_.Add(candidate);
        }
        // 2) Wenn Zahlen-Iteration nicht reicht (extrem belegter Slug), fülle
        //    mit einem kurzen Random-Suffix auf — 3 Zeichen aus dem
        //    reduzierten Alphabet reichen für Nicht-Kollision bei 27k+ Slots.
        for (int attempt = 0; attempt < 12 && out_.Count < count; attempt++)
        {
            var suffix = GenerateRandom(3);
            var candidate = $"{stem}-{suffix}";
            if (!IsValid(candidate)) continue;
            if (await IsAvailableAsync(candidate, ct) && !out_.Contains(candidate))
                out_.Add(candidate);
        }
        return out_;
    }
}
