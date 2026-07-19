namespace NimShare.Core.Entities;

/// <summary>
/// A digital-signature certificate the user owns — either self-issued via the
/// UI ("Zertifikat erstellen") or an imported PKCS#12 they already had
/// ("Zertifikat hochladen"). Encrypted PFX bytes are stored inside the app;
/// the user's PFX password never leaves the browser and is not persisted.
/// </summary>
public class SigningCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; set; }
    public User? Owner { get; set; }

    /// <summary>Human-friendly label shown in the sign-flow picker.</summary>
    public string Name { get; set; } = "";

    public string SubjectCommonName { get; set; } = "";
    public string Issuer { get; set; } = "";

    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>SHA-1 thumbprint hex — used to detect duplicates on re-import.</summary>
    public string Thumbprint { get; set; } = "";

    /// <summary>Whether this certificate was created inside NimShare (true) or
    /// imported from an existing PFX (false).</summary>
    public bool IsSelfIssued { get; set; }

    /// <summary>DataProtection-encrypted PFX bytes. Contains BOTH the cert and
    /// its private key. Decrypt only inside the sign path.</summary>
    public byte[] PfxDataEncrypted { get; set; } = Array.Empty<byte>();

    /// <summary>Marks the user's default cert (auto-selected in the sign
    /// wizard when they haven't picked one).</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public int UseCount { get; set; }
}
