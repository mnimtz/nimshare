namespace NimShare.Core.Entities;

/// <summary>OnlyOffice / Collabora Document Server config. Singleton row.</summary>
public class OfficeSettings
{
    public Guid Id { get; set; } = new Guid("00000000-0000-0000-0000-000000000900");
    public bool Enabled { get; set; }
    /// <summary>Base URL of the Document Server, e.g. https://office.example.com.</summary>
    public string? DocumentServerUrl { get; set; }
    /// <summary>JWT shared secret (data-protected before persist).</summary>
    public string? JwtSecretEncrypted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
