using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace NimShare.Api.Services;

public class StorageOptions
{
    public const string SectionName = "Storage";

    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";
    public string ContainerName { get; set; } = "files";
    public int DownloadSasTtlSeconds { get; set; } = 60;
    // Uploaders on slow home connections regularly hit the 30-min ceiling on
    // multi-gigabyte files; bumping to 2 h absorbs the tail without becoming
    // a security risk (the SAS still targets one exact blob path).
    public int UploadSasTtlMinutes { get; set; } = 120;

    /// <summary>Account name — required when using OAuth (Managed Identity) instead of connection string.</summary>
    public string? AccountName { get; set; }
    public bool UseManagedIdentity { get; set; }
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _serviceClient;
    private readonly StorageOptions _options;
    private readonly StorageSharedKeyCredential? _sharedKey;

    public BlobStorageService(IOptions<StorageOptions> options)
    {
        _options = options.Value;

        if (_options.UseManagedIdentity && !string.IsNullOrWhiteSpace(_options.AccountName))
        {
            var uri = new Uri($"https://{_options.AccountName}.blob.core.windows.net");
            _serviceClient = new BlobServiceClient(uri, new Azure.Identity.DefaultAzureCredential());
            _sharedKey = null;
        }
        else
        {
            _serviceClient = new BlobServiceClient(_options.ConnectionString);
            _sharedKey = TryExtractSharedKey(_options.ConnectionString);
        }
    }

    public async Task EnsureContainerAsync(CancellationToken ct = default)
    {
        var container = _serviceClient.GetBlobContainerClient(_options.ContainerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
    }

    public UploadTicket CreateUploadTicket(string blobPath, TimeSpan? ttl = null)
    {
        var container = _serviceClient.GetBlobContainerClient(_options.ContainerName);
        var blob = container.GetBlobClient(blobPath);

        var expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(_options.UploadSasTtlMinutes));
        var sas = new BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expires,
        };
        sas.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

        var uri = BuildSasUri(blob, sas);
        return new UploadTicket(uri, "PUT", expires);
    }

    public Uri CreateInlineSas(string blobPath, string contentType, TimeSpan? ttl = null)
    {
        var container = _serviceClient.GetBlobContainerClient(_options.ContainerName);
        var blob = container.GetBlobClient(blobPath);
        var expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromSeconds(_options.DownloadSasTtlSeconds));
        var sas = new BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expires,
            ContentDisposition = "inline",
            ContentType = contentType,
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        return BuildSasUri(blob, sas);
    }

    public Uri CreateDownloadSas(string blobPath, string originalFilename, string contentType, TimeSpan? ttl = null)
    {
        var container = _serviceClient.GetBlobContainerClient(_options.ContainerName);
        var blob = container.GetBlobClient(blobPath);

        var expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromSeconds(_options.DownloadSasTtlSeconds));

        var sas = new BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expires,
            ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(originalFilename)}",
            ContentType = contentType,
        };
        sas.SetPermissions(BlobSasPermissions.Read);

        return BuildSasUri(blob, sas);
    }

    public async Task<(bool Exists, long SizeBytes, string? ContentType)> ProbeAsync(string blobPath, CancellationToken ct = default)
    {
        var blob = _serviceClient.GetBlobContainerClient(_options.ContainerName).GetBlobClient(blobPath);
        var exists = await blob.ExistsAsync(ct);
        if (!exists.Value) return (false, 0, null);

        var props = await blob.GetPropertiesAsync(cancellationToken: ct);
        return (true, props.Value.ContentLength, props.Value.ContentType);
    }

    public async Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        var blob = _serviceClient.GetBlobContainerClient(_options.ContainerName).GetBlobClient(blobPath);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task CopyAsync(string sourcePath, string destPath, CancellationToken ct = default)
    {
        var container = _serviceClient.GetBlobContainerClient(_options.ContainerName);
        var src = container.GetBlobClient(sourcePath);
        var dst = container.GetBlobClient(destPath);
        // Use a read-SAS on the source so the copy operation doesn't need
        // extra identity plumbing on the destination side.
        var srcSas = new Azure.Storage.Sas.BlobSasBuilder
        {
            BlobContainerName = _options.ContainerName,
            BlobName = sourcePath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        srcSas.SetPermissions(Azure.Storage.Sas.BlobSasPermissions.Read);
        var srcUri = BuildSasUri(src, srcSas);
        var op = await dst.StartCopyFromUriAsync(srcUri, cancellationToken: ct);
        await op.WaitForCompletionAsync(ct);
    }

    public async Task DownloadToAsync(string blobPath, Stream destination, CancellationToken ct = default)
    {
        var blob = _serviceClient.GetBlobContainerClient(_options.ContainerName).GetBlobClient(blobPath);
        await blob.DownloadToAsync(destination, ct);
    }

    public async Task UploadFromStreamAsync(string blobPath, Stream source, string contentType, CancellationToken ct = default)
    {
        var blob = _serviceClient.GetBlobContainerClient(_options.ContainerName).GetBlobClient(blobPath);
        var opts = new Azure.Storage.Blobs.Models.BlobUploadOptions
        {
            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType }
        };
        await blob.UploadAsync(source, opts, ct);
    }

    public async Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default)
    {
        var blob = _serviceClient.GetBlobContainerClient(_options.ContainerName).GetBlobClient(blobPath);
        var exists = await blob.ExistsAsync(ct);
        return exists.Value;
    }

    private Uri BuildSasUri(BlobClient blob, BlobSasBuilder sas)
    {
        string sasQuery;
        if (_sharedKey is not null)
        {
            sasQuery = sas.ToSasQueryParameters(_sharedKey).ToString();
        }
        else
        {
            // Managed-identity path: use a user-delegation key. Delegation keys must be short-lived (max 7d).
            // For simplicity here, we synchronously fetch one per SAS. In production, cache it for its TTL.
            var udk = _serviceClient.GetUserDelegationKey(
                startsOn: DateTimeOffset.UtcNow.AddMinutes(-1),
                expiresOn: sas.ExpiresOn.AddMinutes(1)).Value;
            sasQuery = sas.ToSasQueryParameters(udk, _serviceClient.AccountName).ToString();
        }
        return new UriBuilder(blob.Uri) { Query = sasQuery }.Uri;
    }

    private static StorageSharedKeyCredential? TryExtractSharedKey(string connectionString)
    {
        // Parse the classic connection string to get AccountName/AccountKey for SAS signing.
        string? accountName = null, accountKey = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Equals("AccountName", StringComparison.OrdinalIgnoreCase)) accountName = kv[1];
            else if (kv[0].Equals("AccountKey", StringComparison.OrdinalIgnoreCase)) accountKey = kv[1];
        }
        if (accountName is null || accountKey is null) return null;
        return new StorageSharedKeyCredential(accountName, accountKey);
    }
}
