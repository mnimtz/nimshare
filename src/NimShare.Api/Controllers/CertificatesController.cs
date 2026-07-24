using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Digital-signature certificate management. Users create self-signed X.509
/// certs in-app, or upload an existing PKCS#12 (.pfx/.p12) they already had.
/// PFX bytes are DataProtection-encrypted at rest; the PFX password itself is
/// never persisted (needed only to unwrap the PKCS#12 on import).
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/certificates")]
public class CertificatesApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IDataProtector _protector;
    private readonly IInstanceCaService _ca;
    private readonly IStringLocalizer<SharedResources> _l;
    private readonly ILogger<CertificatesApiController> _log;

    public CertificatesApiController(NimShareDbContext db, ICurrentUserService users,
        IDataProtectionProvider dp, IInstanceCaService ca,
        IStringLocalizer<SharedResources> l,
        ILogger<CertificatesApiController> log)
    {
        _db = db; _users = users; _ca = ca; _l = l; _log = log;
        _protector = dp.CreateProtector("NimShare.SigningCertificate.v1");
    }

    public record CertDto(Guid Id, string Name, string SubjectCommonName, string Issuer,
        DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Thumbprint,
        bool IsSelfIssued, bool IsDefault, DateTimeOffset? LastUsedAt, int UseCount,
        DateTimeOffset CreatedAt, bool IsExpired);
    public record GenerateReq(string Name, string CommonName, string? Organization,
        string? Country, int ValidityYears, bool SetAsDefault);
    public record ImportReq(string Name, string PfxBase64, string Password, bool SetAsDefault);

    private static CertDto ToDto(SigningCertificate c) => new(
        c.Id, c.Name, c.SubjectCommonName, c.Issuer, c.NotBefore, c.NotAfter,
        c.Thumbprint, c.IsSelfIssued, c.IsDefault, c.LastUsedAt, c.UseCount,
        c.CreatedAt, c.NotAfter < DateTimeOffset.UtcNow);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var now = DateTimeOffset.UtcNow;
        // Direct-to-DTO projection — avoids leaking a partially-populated
        // SigningCertificate entity (PfxDataEncrypted = null!) that a future
        // caller could accidentally re-attach and blank out.
        var items = await _db.SigningCertificates
            .Where(c => c.OwnerUserId == me.Id)
            .OrderByDescending(c => c.IsDefault).ThenByDescending(c => c.CreatedAt)
            .Select(c => new CertDto(
                c.Id, c.Name, c.SubjectCommonName, c.Issuer,
                c.NotBefore, c.NotAfter, c.Thumbprint,
                c.IsSelfIssued, c.IsDefault, c.LastUsedAt, c.UseCount,
                c.CreatedAt, c.NotAfter < now))
            .ToListAsync(ct);
        return Ok(items);
    }

    /// <summary>Generate a new user signing certificate, signed by the NimShare
    /// Instance-Root-CA (v1.10.153 Weg A). Recipients who trust the NimShare-
    /// Root once will trust every future cert of any user on this instance.
    /// The `IsSelfIssued` flag is kept semantically as "created in-app" (vs.
    /// imported from an external PFX) — the actual Issuer is the CA.</summary>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.CommonName))
            return Problem(statusCode: 422, title: _l["certs.err.name_cn_required"].Value);
        var years = Math.Clamp(req.ValidityYears <= 0 ? 3 : req.ValidityYears, 1, 20);

        try
        {
            var parts = new List<string> { $"CN={EscapeDn(req.CommonName)}" };
            if (!string.IsNullOrWhiteSpace(req.Organization)) parts.Add($"O={EscapeDn(req.Organization)}");
            if (!string.IsNullOrWhiteSpace(req.Country)) parts.Add($"C={EscapeDn(req.Country[..Math.Min(2, req.Country.Length)])}");
            var dn = new X500DistinguishedName(string.Join(", ", parts));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.AddYears(years);
            var signed = await _ca.SignUserCertificateAsync(dn, notBefore, notAfter, ct);
            var wrapped = _protector.Protect(BundleWithPassword(signed.PfxBytes, signed.PfxPassword));

            var entity = new SigningCertificate
            {
                OwnerUserId = me.Id,
                Name = req.Name.Trim(),
                SubjectCommonName = ExtractCn(signed.SubjectDn) ?? req.CommonName,
                Issuer = signed.IssuerDn,
                NotBefore = signed.NotBefore,
                NotAfter = signed.NotAfter,
                Thumbprint = signed.Thumbprint,
                IsSelfIssued = true, // in-app-generated (Semantik seit v1.10.153: „in-app", nicht mehr wörtlich self-signed)
                PfxDataEncrypted = wrapped,
            };
            await SaveAsync(entity, req.SetAsDefault, ct);
            return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToDto(entity));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Certificate generation failed for user {UserId}", me.Id);
            var msg = ex.Message?.Length > 240 ? ex.Message[..240] : ex.Message;
            return Problem(statusCode: 500, title: _l["certs.err.generate_failed"].Value,
                detail: $"{ex.GetType().Name}: {msg}");
        }
    }

    private static string? ExtractCn(string? subjectName)
    {
        if (string.IsNullOrEmpty(subjectName)) return null;
        // X500 DN format: CN=Foo, O=Bar, C=DE — commas inside CN values are
        // escaped with backslash, so a plain Split(',') would truncate a name
        // like "Nimtz, Marcus". Walk RDNs manually.
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool escaped = false;
        foreach (var ch in subjectName)
        {
            if (escaped) { current.Append(ch); escaped = false; continue; }
            if (ch == '\\') { escaped = true; continue; }
            if (ch == ',') { parts.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString().Trim());
        var cn = parts.FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
        return cn?.Substring(3).Trim();
    }

    /// <summary>Import an existing PKCS#12 (PFX/.p12). The password is
    /// required to unwrap the private key; we store the PFX under our own
    /// encryption afterwards.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.PfxBase64))
            return Problem(statusCode: 422, title: _l["certs.err.name_pfx_required"].Value);
        byte[] rawPfx;
        try { rawPfx = Convert.FromBase64String(req.PfxBase64); }
        catch { return Problem(statusCode: 422, title: _l["certs.err.pfx_not_base64"].Value); }
        if (rawPfx.Length > 512 * 1024)
            return Problem(statusCode: 413, title: _l["certs.err.pfx_too_large"].Value);

        X509Certificate2 cert;
        try
        {
            // Load only to validate password + extract metadata. We keep the
            // *raw* PFX bytes (wrapped with the caller's password) for later
            // decryption — never rely on X509Certificate2 as a container after
            // this request ends because Windows/Linux stores it in transient
            // key blobs that go away.
#pragma warning disable SYSLIB0057 // constructor still supported; new API not yet ubiquitous
            cert = new X509Certificate2(rawPfx, req.Password ?? "",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException ex)
        {
            _log.LogInformation(ex, "PFX decryption failed for user {UserId}", me.Id);
            return Problem(statusCode: 422, title: _l["certs.err.pfx_decrypt"].Value);
        }
        using var _ = cert;
        if (!cert.HasPrivateKey)
            return Problem(statusCode: 422, title: _l["certs.err.no_private_key"].Value);

        var wrapped = _protector.Protect(BundleWithPassword(rawPfx, req.Password ?? ""));
        var entity = new SigningCertificate
        {
            OwnerUserId = me.Id,
            Name = req.Name.Trim(),
            SubjectCommonName = cert.SubjectName.Name?.Split(',')
                .Select(s => s.Trim())
                .FirstOrDefault(s => s.StartsWith("CN="))
                ?.Substring(3) ?? cert.SubjectName.Name ?? req.Name,
            Issuer = cert.IssuerName.Name ?? "unknown",
            NotBefore = cert.NotBefore.ToUniversalTime(),
            NotAfter = cert.NotAfter.ToUniversalTime(),
            Thumbprint = cert.Thumbprint,
            IsSelfIssued = false,
            PfxDataEncrypted = wrapped,
        };
        // If this exact certificate is already imported, refuse rather than
        // silently duplicating — the thumbprint index enforces uniqueness per
        // owner anyway.
        var exists = await _db.SigningCertificates
            .AnyAsync(c => c.OwnerUserId == me.Id && c.Thumbprint == cert.Thumbprint, ct);
        if (exists)
            return Problem(statusCode: 409, title: _l["certs.err.duplicate"].Value);
        await SaveAsync(entity, req.SetAsDefault, ct);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToDto(entity));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var c = await _db.SigningCertificates.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        return c is null ? NotFound() : Ok(ToDto(c));
    }

    /// <summary>Returns the caller's default signing certificate (or their
    /// most-recently-created one if no default is set). Used by the Sign UI
    /// to render a certificate-stamp preview (v1.10.15). Only public cert
    /// metadata — the PFX bytes never leave the server.</summary>
    [HttpGet("default")]
    public async Task<IActionResult> GetDefault(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var now = DateTimeOffset.UtcNow;
        var c = await _db.SigningCertificates
            .Where(x => x.OwnerUserId == me.Id && x.NotAfter > now)
            .OrderByDescending(x => x.IsDefault)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (c is null) return NotFound();
        return Ok(ToDto(c));
    }

    [HttpPost("{id:guid}/set-default")]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var c = await _db.SigningCertificates.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (c is null) return NotFound();
        await UnsetOtherDefaultsAsync(me.Id, ct);
        c.IsDefault = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var c = await _db.SigningCertificates.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (c is null) return NotFound();
        // v1.10.153: Prüfe Link-Nutzung — Löschen verhindert wenn das Zert an
        // Share-Links oder Upload-Anforderungen hängt. Sonst würde die Landing
        // eines schon versendeten Links plötzlich das Signer-Badge verlieren
        // („✓ Verifiziert" → nichts), was für Empfänger irritierend ist.
        var shareLinks = await _db.ShareLinks.CountAsync(l => l.SigningCertificateId == id, ct);
        var uploadLinks = await _db.UploadRequests.CountAsync(l => l.SigningCertificateId == id, ct);
        var total = shareLinks + uploadLinks;
        if (total > 0)
        {
            return Problem(statusCode: 409,
                title: _l["certs.err.in_use_title"].Value,
                detail: string.Format(_l["certs.err.in_use_detail"].Value, total, shareLinks, uploadLinks));
        }
        _db.SigningCertificates.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Export the certificate's *public* portion as PEM so the user
    /// can send it to a counterparty for verification. The private key never
    /// leaves the server this way.</summary>
    [HttpGet("{id:guid}/public")]
    public async Task<IActionResult> ExportPublic(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var c = await _db.SigningCertificates.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (c is null) return NotFound();
        var (pfxBytes, password) = UnbundleWithPassword(_protector.Unprotect(c.PfxDataEncrypted));
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(pfxBytes, password, X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        var pem = "-----BEGIN CERTIFICATE-----\n" +
            Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks) +
            "\n-----END CERTIFICATE-----\n";
        return File(System.Text.Encoding.UTF8.GetBytes(pem), "application/x-pem-file",
            $"{c.SubjectCommonName}.cer");
    }

    private async Task SaveAsync(SigningCertificate entity, bool setAsDefault, CancellationToken ct)
    {
        if (setAsDefault) await UnsetOtherDefaultsAsync(entity.OwnerUserId, ct);
        entity.IsDefault = setAsDefault;
        _db.SigningCertificates.Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    private async Task UnsetOtherDefaultsAsync(Guid ownerId, CancellationToken ct)
    {
        var others = await _db.SigningCertificates
            .Where(c => c.OwnerUserId == ownerId && c.IsDefault).ToListAsync(ct);
        foreach (var o in others) o.IsDefault = false;
    }

    private static string EscapeDn(string s) =>
        s.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("=", "\\=");

    // We need both the PFX bytes AND the password used to lock it, so the
    // signer path can later re-open the PFX. Concatenate password + \0 + pfx.
    private static byte[] BundleWithPassword(byte[] pfx, string password)
    {
        var pwBytes = System.Text.Encoding.UTF8.GetBytes(password);
        var buf = new byte[4 + pwBytes.Length + pfx.Length];
        BitConverter.GetBytes(pwBytes.Length).CopyTo(buf, 0);
        pwBytes.CopyTo(buf, 4);
        pfx.CopyTo(buf, 4 + pwBytes.Length);
        return buf;
    }
    private static (byte[] pfx, string password) UnbundleWithPassword(byte[] buf)
    {
        var len = BitConverter.ToInt32(buf, 0);
        var password = System.Text.Encoding.UTF8.GetString(buf, 4, len);
        var pfx = buf.AsSpan(4 + len).ToArray();
        return (pfx, password);
    }
}

[Authorize]
public class CertificatesPageController : Controller
{
    [HttpGet("/signatures/certificates")]
    public IActionResult Index() => View("Index");
}
