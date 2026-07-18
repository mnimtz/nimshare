using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NimShare.Api.Services;

public class IpHashOptions
{
    public const string SectionName = "IpHash";

    /// <summary>Server-side salt. Rotate periodically. Loaded from Key Vault in production.</summary>
    public string Salt { get; set; } = "change-me-in-production";
}

public interface IIpHashService
{
    string Hash(string ipAddress);
}

public class IpHashService : IIpHashService
{
    private readonly byte[] _saltBytes;

    public IpHashService(IOptions<IpHashOptions> opt)
    {
        _saltBytes = Encoding.UTF8.GetBytes(opt.Value.Salt);
    }

    public string Hash(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) ipAddress = "unknown";
        using var mac = new HMACSHA256(_saltBytes);
        var digest = mac.ComputeHash(Encoding.UTF8.GetBytes(ipAddress));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
