using OtpNet;

namespace NimShare.Api.Services;

/// <summary>
/// TOTP (RFC 6238) helper used by the 2FA setup + verify flow. Wraps OtpNet
/// so callers stay ignorant of the byte-array-vs-string dance.
/// </summary>
public interface ITotpService
{
    /// <summary>Generate a fresh base32 secret (16 bytes / 128 bit as recommended by RFC 4226 §4).</summary>
    string GenerateSecret();

    /// <summary>Return the URI encoded into the setup QR code (otpauth://…).</summary>
    string BuildOtpAuthUri(string secretBase32, string accountName, string issuer);

    /// <summary>Check a 6-digit code; ±1 step window for clock drift.</summary>
    bool Verify(string secretBase32, string code);
}

public class TotpService : ITotpService
{
    public string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    public string BuildOtpAuthUri(string secretBase32, string accountName, string issuer)
    {
        var e = Uri.EscapeDataString;
        return $"otpauth://totp/{e(issuer)}:{e(accountName)}?secret={secretBase32}&issuer={e(issuer)}&digits=6&period=30";
    }

    public bool Verify(string secretBase32, string code)
    {
        if (string.IsNullOrWhiteSpace(secretBase32) || string.IsNullOrWhiteSpace(code)) return false;
        code = code.Replace(" ", "").Trim();
        try
        {
            var bytes = Base32Encoding.ToBytes(secretBase32);
            var totp = new Totp(bytes);
            return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
        }
        catch { return false; }
    }
}
