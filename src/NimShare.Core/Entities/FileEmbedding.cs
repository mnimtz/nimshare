namespace NimShare.Core.Entities;

/// <summary>
/// Per-file vector embedding for semantic search. One row per file; overwritten
/// when the model changes or on explicit re-embed.
/// </summary>
public class FileEmbedding
{
    public Guid FileId { get; set; }
    public StorageFile File { get; set; } = null!;

    /// <summary>Model that produced the vector (e.g. "text-embedding-3-small"). Rows with different models coexist but only the current one is queried.</summary>
    public string Model { get; set; } = "";

    /// <summary>Raw float32 vector, little-endian, packed. Length depends on model (1536 for OpenAI small, 768 for Gemini text-embedding-004).</summary>
    public byte[] Vector { get; set; } = Array.Empty<byte>();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
