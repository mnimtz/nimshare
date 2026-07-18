namespace NimShare.Api.Services;

public interface IBlobStorageService
{
    /// <summary>
    /// Creates the container if it doesn't exist yet. Idempotent — safe to call on every startup.
    /// </summary>
    Task EnsureContainerAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a write-SAS URL for a specific blob path. TTL is short (default 30 min) to give
    /// the client time to upload chunked but not much longer.
    /// </summary>
    UploadTicket CreateUploadTicket(string blobPath, TimeSpan? ttl = null);

    /// <summary>
    /// Returns a read-SAS URL for a specific blob, with response headers overridden so the
    /// browser prompts a download with the original filename and content-type.
    /// </summary>
    Uri CreateDownloadSas(string blobPath, string originalFilename, string contentType, TimeSpan? ttl = null);

    /// <summary>Returns true if the blob exists and its size in bytes. Used at upload completion.</summary>
    Task<(bool Exists, long SizeBytes, string? ContentType)> ProbeAsync(string blobPath, CancellationToken ct = default);

    /// <summary>Deletes a blob (idempotent — missing blob is not an error).</summary>
    Task DeleteAsync(string blobPath, CancellationToken ct = default);

    /// <summary>Streams the blob content to <paramref name="destination"/> — used for the ZIP folder download.</summary>
    Task DownloadToAsync(string blobPath, Stream destination, CancellationToken ct = default);
}

public record UploadTicket(Uri UploadUrl, string Method, DateTimeOffset ExpiresAt);
