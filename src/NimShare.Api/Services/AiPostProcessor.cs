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
    // v1.10.68: Concurrency-Gate. Vorher konnten unbeschränkt viele parallele
    // AI-Aufrufe laufen (jeder mit eigenem DB-Scope + HTTP-Call). Bei einem
    // Bulk-Upload von z.B. 20 Files → 20 Tasks blockierten alle den ThreadPool
    // und die SQLite-Writer-Lock-Queue → User-Requests hingen minutenlang.
    // Jetzt: max 2 gleichzeitige AI-Runs, alle weiteren warten in Reihe.
    // Semaphore statt Channel gewählt weil kein Fairness-Requirement und
    // fire-and-forget Semantik erhalten bleibt.
    private static readonly SemaphoreSlim _concurrency = new(2, 2);

    public AiPostProcessor(IServiceScopeFactory scopes, ILogger<AiPostProcessor> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public void QueueForFile(Guid fileId)
    {
        _ = Task.Run(async () =>
        {
            await _concurrency.WaitAsync();
            try { await RunAsync(fileId); }
            finally { _concurrency.Release(); }
        });
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

            // v1.10.165: Apple-5.1.1(i) — auch der server-seitige Auto-Tag/
            // Embedding-Pfad muss den Consent des Uploaders respektieren.
            // Ohne Consent: still zurück (keine AI-Verarbeitung, aber die
            // Text-Extraktion oben ist rein lokal und darf weiterhin laufen).
            var owner = await db.Users.SingleOrDefaultAsync(u => u.Id == file.OwnerId);
            if (owner is null || owner.AiConsentedAt is null)
            {
                _log.LogInformation("AI post-processing skipped for file {FileId}: owner {OwnerId} has not consented to AI processing.", fileId, file.OwnerId);
                return;
            }

            var provider = await gateway.CreateProviderAsync();
            var text = await gateway.ExtractTextAsync(file.BlobPath, file.ContentType, blobs);

            // Persist the extracted text for classic keyword search — same
            // pass, no extra download. Truncated to match the column length.
            if (!string.IsNullOrEmpty(text))
            {
                file.ExtractedText = text.Length > 200_000 ? text[..200_000] : text;
                await db.SaveChangesAsync();
            }

            if (string.IsNullOrEmpty(text))
            {
                // Even without extracted content we can still embed the filename for search.
                text = file.Name;
            }

            if (doTags || (doRisk && file.Scope == FileScope.Public))
            {
                // v1.10.192: Tags in der Sprache des File-Owners (Web-Sprach-
                // wahl persistiert in User.PreferredCulture). Vorher waren die
                // Tags immer englisch, egal was der User eingestellt hatte.
                var tagLang = string.IsNullOrWhiteSpace(owner.PreferredCulture) ? "en" : owner.PreferredCulture;
                var cls = await provider.ClassifyAsync(file.Name, text, tagLang);
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
                var embedResult = await provider.EmbedAsync($"{file.Name}\n\n{(text.Length > 2000 ? text[..2000] : text)}");
                if (embedResult is { } er && er.Vector.Length > 0)
                {
                    var (vec, embedModel) = er;
                    var bytes = new byte[vec.Length * 4];
                    Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
                    // v2.0.4: das TATSÄCHLICH verwendete Embedding-Modell
                    // speichern (nicht mehr settings.Model — das ist der
                    // konfigurierte Chat-Completion-Modellname, z.B. bei
                    // Gemini eine ganz andere Zeichenkette als die Embedding-
                    // Kandidaten aus dem Fallback in EmbedAsync). Nur so kann
                    // RetrieveHitsAsync später prüfen, ob Query- und
                    // Index-Vektor überhaupt aus demselben Modell stammen.
                    var existing = await db.FileEmbeddings.SingleOrDefaultAsync(e => e.FileId == file.Id);
                    if (existing is null)
                    {
                        db.FileEmbeddings.Add(new FileEmbedding
                        {
                            FileId = file.Id,
                            Model = embedModel,
                            Vector = bytes,
                        });
                    }
                    else
                    {
                        existing.Model = embedModel;
                        existing.Vector = bytes;
                        existing.CreatedAt = DateTimeOffset.UtcNow;
                    }
                    await db.SaveChangesAsync();
                    _log.LogInformation("Embedding created/updated for {FileId} ({Dim} dimensions, model {Model}).", fileId, vec.Length, embedModel);
                }
                else
                {
                    // v1.10.30: Provider hat kein Vector geliefert. Provider-LastError
                    // reflektiert warum (HTTP 400 API key not valid, quota, model
                    // not found, safety). Bislang war das ein stiller Fehlschlag,
                    // Reindex-Runs erzeugten 0 Embeddings ohne Spur.
                    var openErr = (provider as OpenAiProvider)?.LastError;
                    var geminiErr = (provider as GeminiProvider)?.LastError;
                    _log.LogWarning(
                        "Embed returned null for {FileId}. Provider={ProviderType} Model={Model} OpenErr={OpenErr} GeminiErr={GeminiErr}",
                        fileId, provider.GetType().Name, settings.Model, openErr, geminiErr);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI post-process failed for file {FileId}", fileId);
        }
    }
}
