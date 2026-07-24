using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// Reads the PUBLIC cert bytes out of an encrypted SigningCertificate row.
/// The private key never leaves this service (the returned DER is public-
/// only). Introduced in v1.10.153 so the /s/{slug}/signer-cert.cer and
/// /u/{slug}/signer-cert.cer public download endpoints can share the same
/// unwrap logic without dragging the whole CertificatesApiController in.
/// </summary>
public interface ISignerCertReader
{
    byte[] GetPublicDer(SigningCertificate cert);
}

public class SignerCertReader : ISignerCertReader
{
    private readonly IDataProtector _protector;
    public SignerCertReader(IDataProtectionProvider dp)
    {
        _protector = dp.CreateProtector("NimShare.SigningCertificate.v1");
    }

    public byte[] GetPublicDer(SigningCertificate cert)
    {
        var buf = _protector.Unprotect(cert.PfxDataEncrypted);
        var pwLen = BitConverter.ToInt32(buf, 0);
        var password = System.Text.Encoding.UTF8.GetString(buf, 4, pwLen);
        var pfx = buf.AsSpan(4 + pwLen).ToArray();
#pragma warning disable SYSLIB0057
        using var x = new X509Certificate2(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        return x.RawData;
    }
}
