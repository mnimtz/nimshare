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
    private readonly IStringLocalizer<SharedResources> _l;
    private readonly ILogger<CertificatesApiController> _log;

    public CertificatesApiController(NimShareDbContext db, ICurrentUserService users,
        IDataProtectionProvider dp, IStringLocalizer<SharedResources> l,
        ILogger<CertificatesApiController> log)
    {
        _db = db; _users = users; _l = l; _log = log;
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

    /// <summary>Self-signed certificate — good for internal audit trails and
    /// visual sign-here stamps. Not accepted by external CA validation.</summary>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.CommonName))
            return Problem(statusCode: 422, title: _l["certs.err.name_cn_required"].Value);
        var years = Math.Clamp(req.ValidityYears <= 0 ? 3 : req.ValidityYears, 1, 20);

        try
        {
            // Build a distinguished name: CN, optional O, optional C.
            var parts = new List<string> { $"CN={EscapeDn(req.CommonName)}" };
            if (!string.IsNullOrWhiteSpace(req.Organization)) parts.Add($"O={EscapeDn(req.Organization)}");
            if (!string.IsNullOrWhiteSpace(req.Country)) parts.Add($"C={EscapeDn(req.Country[..Math.Min(2, req.Country.Length)])}");
            var dn = new X500DistinguishedName(string.Join(", ", parts));

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            // Basic constraints — mark as end-entity, not a CA.
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            // Digital signature + non-repudiation (used by CMS/PKCS#7 signing).
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.4") /* Email protection */,
                                    new("1.3.6.1.4.1.311.10.3.12") /* Document signing */ }, false));
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.AddYears(years);
            using var built = request.CreateSelfSigned(notBefore, notAfter);

            var pfxPassword = Guid.NewGuid().ToString("N"); // ephemeral, only used to serialise
            // Export can fail on Linux containers if the private key handle
            // became ephemeral; re-import as Exportable in the same process
            // then export again. Belt + braces for portability.
            byte[] pfxBytes;
            try
            {
                pfxBytes = built.Export(X509ContentType.Pfx, pfxPassword);
            }
            catch (CryptographicException)
            {
                // Fallback path: rebuild the PFX from RSA + cert bytes directly.
                using var certOnly = new X509Certificate2(built.RawData);
                using var withKey = certOnly.CopyWithPrivateKey(rsa);
#pragma warning disable SYSLIB0057
                using var reimport = new X509Certificate2(
                    withKey.Export(X509ContentType.Pfx, pfxPassword),
                    pfxPassword,
                    X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
                pfxBytes = reimport.Export(X509ContentType.Pfx, pfxPassword);
            }
            var wrapped = _protector.Protect(BundleWithPassword(pfxBytes, pfxPassword));

            var entity = new SigningCertificate
            {
                OwnerUserId = me.Id,
                Name = req.Name.Trim(),
                SubjectCommonName = ExtractCn(built.SubjectName.Name) ?? req.CommonName,
                Issuer = built.IssuerName.Name ?? "self",
                NotBefore = notBefore,
                NotAfter = notAfter,
                Thumbprint = built.Thumbprint,
                IsSelfIssued = true,
                PfxDataEncrypted = wrapped,
            };
            await SaveAsync(entity, req.SetAsDefault, ct);
            return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToDto(entity));
        }
        catch (Exception ex)
        {
            // Log the full exception (type + stack) server-side; return the
            // exception-type + short message to the client. Cert-gen used to
            // return a generic 500 with no clue why (RSA/Pfx export failures
            // on hardened Linux containers are hard to diagnose blind).
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
