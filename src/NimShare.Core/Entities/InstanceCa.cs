namespace NimShare.Core.Entities;

/// <summary>
/// The one-and-only Root-CA of this NimShare instance. Every in-app-generated
/// user signing certificate (see <see cref="SigningCertificate"/>) is signed by
/// this CA — that way, once a recipient trusts the NimShare-Root once (public
/// .cer download on any signed landing), every future signed link from any
/// user on this instance validates automatically without further prompts.
///
/// Singleton: the app always operates on the single active row; older rows
/// (should we ever rotate) are kept for historical validation of already-issued
/// user certs.
///
/// Introduced in v1.10.153 with migration V186_InstanceCa.
/// </summary>
public class InstanceCa
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable CA name (goes into cert Subject/Issuer).</summary>
    public string Name { get; set; } = "";

    /// <summary>Full X.500 DN of the CA subject (== issuer for a self-signed root).</summary>
    public string SubjectDn { get; set; } = "";

    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>SHA-1 thumbprint hex of the CA certificate.</summary>
    public string Thumbprint { get; set; } = "";

    /// <summary>DataProtection-encrypted PFX bytes (contains private key too).
    /// Encoded as 4-byte length prefix + UTF-8 password + PFX bytes, same as
    /// the SigningCertificate.PfxDataEncrypted layout.</summary>
    public byte[] PfxDataEncrypted { get; set; } = Array.Empty<byte>();

    /// <summary>When this CA became active. Only one active CA at a time.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
