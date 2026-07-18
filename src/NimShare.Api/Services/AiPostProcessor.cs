using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// Kicked from FilesController.Complete() — runs classify + embed on the newly
/// uploaded file if the admin has flipped the flags in /settings/ai. Fire-and-
/// forget: any failure is logged, never bubbles back to the uploader.
/// </summary>
public interface IAiPostProcessor
{
    void QueueForFile(Guid fileId);
}

public class AiPostProcessor : IAiPostProcessor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AiPostProcessor> _log;

    public AiPostProcessor(IServiceScopeFactory scopes, ILogger<AiPostProcessor> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public void QueueForFile(Guid fileId)
    {
        _ = Task.Run(() => RunAsync(fileId));
    }

    private async Task RunAsync(Guid fileId)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
            var gateway = scope.ServiceProvider.GetRequiredService<IAiGatewayService>();
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
            var settings = await gateway.LoadAsync();
            if (settings.Provider == AiProvider.Disabled) return;
            var doTags = settings.EnableSmartTags;
            var doRisk = settings.EnableContentRiskDetection;
            var doEmbed = settings.EnableSemanticSearch;
            if (!doTags && !doRisk && !doEmbed) return;

            var file = await db.Files.SingleOrDefaultAsync(f => f.Id == fileId);
            if (file is null || file.Status != StorageFileStatus.Ready) return;

            var provider = await gateway.CreateProviderAsync();
            var text = await gateway.ExtractTextAsync(file.BlobPath, file.ContentType, blobs);
            if (string.IsNullOrEmpty(text))
            {
                // Even without extracted content we can still embed the filename for search.
                text = file.Name;
            }

            if (doTags || (doRisk && file.Scope == FileScope.Public))
            {
                var cls = await provider.ClassifyAsync(file.Name, text);
                if (cls is not null)
                {
                    if (doTags && cls.Tags.Length > 0)
                        file.AiTags = string.Join(",", cls.Tags);
                    if (doRisk && file.Scope == FileScope.Public && !string.IsNullOrEmpty(cls.RiskFlag))
                        file.AiRiskFlag = cls.RiskFlag;
                    await db.SaveChangesAsync();
                }
            }

            if (doEmbed)
            {
                var vec = await provider.EmbedAsync($"{file.Name}\n\n{(text.Length > 2000 ? text[..2000] : text)}");
                if (vec is not null && vec.Length > 0)
                {
                    var bytes = new byte[vec.Length * 4];
                    Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
                    var existing = await db.FileEmbeddings.SingleOrDefaultAsync(e => e.FileId == file.Id);
                    if (existing is null)
                    {
                        db.FileEmbeddings.Add(new FileEmbedding
                        {
                            FileId = file.Id,
                            Model = settings.Model ?? "default",
                            Vector = bytes,
                        });
                    }
                    else
                    {
                        existing.Model = settings.Model ?? "default";
                        existing.Vector = bytes;
                        existing.CreatedAt = DateTimeOffset.UtcNow;
                    }
                    await db.SaveChangesAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI post-process failed for file {FileId}", fileId);
        }
    }
}
