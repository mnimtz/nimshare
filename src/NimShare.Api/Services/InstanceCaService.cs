using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// Manager for this instance's Root-CA. Every in-app-created user signing
/// certificate is signed by the CA returned here — so a recipient who trusts
/// the NimShare-Root once (public .cer download on any signed landing page)
/// automatically trusts every future signed link from any user on this
/// instance without further prompts.
///
/// Introduced in v1.10.153 (Weg A "internal PKI").
/// </summary>
public interface IInstanceCaService
{
    /// <summary>Returns the active CA row, creating it on first use.</summary>
    Task<InstanceCa> GetOrCreateAsync(CancellationToken ct);

    /// <summary>Public DER bytes of the CA cert — for the /nimshare-root.cer
    /// download endpoint. The private key never leaves the server.</summary>
    Task<byte[]> GetPublicCertDerAsync(CancellationToken ct);

    /// <summary>Signs a user certificate request with the active CA. Returns
    /// the finished PFX (containing the user's private key + the CA-signed
    /// cert) and the ephemeral PFX password used to seal it — same shape as
    /// SigningCertificate.PfxDataEncrypted (see BundleWithPassword there).
    /// </summary>
    Task<SignResult> SignUserCertificateAsync(
        X500DistinguishedName subject,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken ct);

    /// <summary>Human-readable Subject-CN of the active CA — used by the
    /// landing page to render "Signed by NimShare Instance CA".</summary>
    Task<string> GetSubjectCommonNameAsync(CancellationToken ct);
}

public record SignResult(
    byte[] PfxBytes,
    string PfxPassword,
    string CertPemPublic,
    string SubjectDn,
    string IssuerDn,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

public class InstanceCaService : IInstanceCaService
{
    private readonly NimShareDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<InstanceCaService> _log;

    // CA validity — long-lived by design; a CA rotation triggers new user
    // certs but old ones remain verifiable via the retired CA row.
    private static readonly TimeSpan CaValidity = TimeSpan.FromDays(365 * 20);

    // Serialisiert First-Boot-Erzeugung app-weit. Ohne Semaphore würden zwei
    // parallele erste Requests beide eine CA-Row anlegen (der Unique-Index
    // ist filtered auf IsActive; SQLite hat aber ohnehin keine filtered
    // uniques, und beide Threads würden gleichzeitig SaveChanges rufen). Das
    // Ergebnis wären zwei aktive CAs, non-deterministischer Signer, und
    // /nimshare-root.cer würde nur eine davon liefern → Empfänger sehen
    // "unknown CA" für Links, die an die andere gekettet sind.
    private static readonly SemaphoreSlim _createGate = new(1, 1);

    public InstanceCaService(NimShareDbContext db, IDataProtectionProvider dp,
        ILogger<InstanceCaService> log)
    {
        _db = db;
        _protector = dp.CreateProtector("NimShare.InstanceCa.v1");
        _log = log;
    }

    public async Task<InstanceCa> GetOrCreateAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await _db.InstanceCas
            .Where(c => c.IsActive && c.NotAfter > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (active is not null) return active;

        await _createGate.WaitAsync(ct);
        try
        {
            // Double-check unter dem Lock — zweiter Warter findet die CA
            // jetzt und erzeugt keine zweite.
            active = await _db.InstanceCas
                .Where(c => c.IsActive && c.NotAfter > now)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (active is not null) return active;

            _log.LogInformation("No active NimShare Instance CA — generating a new one.");
            return await BuildAndPersistAsync(now, ct);
        }
        finally
        {
            _createGate.Release();
        }
    }

    public async Task<byte[]> GetPublicCertDerAsync(CancellationToken ct)
    {
        var ca = await GetOrCreateAsync(ct);
        var (pfx, pw) = UnbundleWithPassword(_protector.Unprotect(ca.PfxDataEncrypted));
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(pfx, pw, X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        // Return the DER-encoded PUBLIC bytes only — this is what recipients
        // import to trust the CA. Never expose the PFX (contains private key).
        return cert.RawData;
    }

    public async Task<string> GetSubjectCommonNameAsync(CancellationToken ct)
    {
        var ca = await GetOrCreateAsync(ct);
        return ExtractCn(ca.SubjectDn) ?? ca.Name;
    }

    public async Task<SignResult> SignUserCertificateAsync(
        X500DistinguishedName subject,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken ct)
    {
        var ca = await GetOrCreateAsync(ct);
        var (caPfx, caPfxPw) = UnbundleWithPassword(_protector.Unprotect(ca.PfxDataEncrypted));

        // The CA cert must be loaded WITH its private key so we can sign.
        // Exportable is required so the returned user PFX can later be
        // re-serialised in the signer path.
#pragma warning disable SYSLIB0057
        using var caCert = new X509Certificate2(caPfx, caPfxPw,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        if (!caCert.HasPrivateKey)
            throw new InvalidOperationException("Instance CA loaded without private key — cannot sign.");

        using var userKey = RSA.Create(2048);
        var req = new CertificateRequest(subject, userKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // End-entity (not another CA).
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        // Digital signature + non-repudiation — needed by CMS/PKCS#7 signing.
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection {
                new("1.3.6.1.5.5.7.3.4") /* Email protection */,
                new("1.3.6.1.4.1.311.10.3.12") /* Document signing */
            }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            caCert, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        // Random 16-byte positive serial (MSB cleared to keep the integer
        // positive; some parsers reject serials that look negative).
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;

        using var signed = req.Create(caCert, notBefore, notAfter, serial);
        // The `signed` cert has no private key attached (Create() only signs
        // the public part). Attach the user's private key so we can export a
        // usable PFX.
        using var withKey = signed.CopyWithPrivateKey(userKey);

        var pfxPassword = Guid.NewGuid().ToString("N");
        byte[] pfxBytes;
        try
        {
            pfxBytes = withKey.Export(X509ContentType.Pfx, pfxPassword);
        }
        catch (CryptographicException)
        {
#pragma warning disable SYSLIB0057
            using var reimport = new X509Certificate2(
                withKey.Export(X509ContentType.Pfx, pfxPassword), pfxPassword,
                X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
            pfxBytes = reimport.Export(X509ContentType.Pfx, pfxPassword);
        }

        var pem = "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(signed.RawData, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----\n";

        return new SignResult(pfxBytes, pfxPassword, pem,
            signed.SubjectName.Name ?? subject.Name,
            signed.IssuerName.Name ?? caCert.SubjectName.Name ?? "",
            signed.Thumbprint,
            signed.NotBefore.ToUniversalTime(),
            signed.NotAfter.ToUniversalTime());
    }

    private async Task<InstanceCa> BuildAndPersistAsync(DateTimeOffset now, CancellationToken ct)
    {
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.Add(CaValidity);
        var subjectDn = "CN=NimShare Instance CA, O=NimShare";
        var dn = new X500DistinguishedName(subjectDn);

        using var caKey = RSA.Create(4096);
        var req = new CertificateRequest(dn, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // CA:TRUE, pathLenConstraint=0 (this CA may sign end-entities but not
        // intermediate CAs — we don't need multi-tier PKI).
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        using var caCert = req.CreateSelfSigned(notBefore, notAfter);
        var pfxPassword = Guid.NewGuid().ToString("N");
        byte[] pfxBytes;
        try
        {
            pfxBytes = caCert.Export(X509ContentType.Pfx, pfxPassword);
        }
        catch (CryptographicException)
        {
#pragma warning disable SYSLIB0057
            using var reimport = new X509Certificate2(
                caCert.Export(X509ContentType.Pfx, pfxPassword), pfxPassword,
                X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
            pfxBytes = reimport.Export(X509ContentType.Pfx, pfxPassword);
        }

        var entity = new InstanceCa
        {
            Name = "NimShare Instance CA",
            SubjectDn = subjectDn,
            NotBefore = notBefore,
            NotAfter = notAfter,
            Thumbprint = caCert.Thumbprint,
            PfxDataEncrypted = _protector.Protect(BundleWithPassword(pfxBytes, pfxPassword)),
            IsActive = true,
        };

        _db.InstanceCas.Add(entity);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("NimShare Instance CA created: Thumbprint={Tp}", caCert.Thumbprint);
        return entity;
    }

    // Same wire format as CertificatesApiController.BundleWithPassword — length-
    // prefix + UTF-8 password + raw PFX. Kept private per-service so the two
    // wrappers never accidentally cross domains.
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

    private static string? ExtractCn(string? dn)
    {
        if (string.IsNullOrEmpty(dn)) return null;
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool escaped = false;
        foreach (var ch in dn)
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
}
