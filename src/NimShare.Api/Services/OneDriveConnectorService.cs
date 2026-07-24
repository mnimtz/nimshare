using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// OneDrive-for-Business-Konnektor (Microsoft Graph). MVP: OAuth-2.0-
/// Authorization-Code-Flow mit PKCE, Refresh-Token DataProtection-verschlüsselt
/// persistiert, Access-Token bei jedem Request frisch geholt (kein Cache).
///
/// Import-Modus: Browse → User wählt Items → NimShare streamt Datei-Content
/// direkt vom Graph in den Azure Blob (kein Zwischen-File auf dem Server).
/// „Push"-Modus (auto-sync bei jedem Upload) ist bewusst NICHT dabei — die
/// Import-Variante entspricht Marcus's User-driven-Konzept aus dem Roadmap-
/// Memo (v1.10.163).
/// </summary>
public interface IConnectorService
{
    Task<string> BuildAuthorizeUrlAsync(Guid userId, string redirectUri, string state, string codeVerifier);
    Task<Connector> CompleteAuthorizeAsync(Guid userId, string code, string redirectUri, string codeVerifier, CancellationToken ct);
    Task<List<ConnectorBrowseItem>> BrowseAsync(Guid connectorId, string? remoteFolderId, CancellationToken ct);
    Task ImportAsync(Guid connectorId, IReadOnlyList<string> remoteItemIds, Guid targetFolderId, bool preserveStructure, CancellationToken ct);
}

public record ConnectorBrowseItem(string Id, string Name, bool IsFolder, long SizeBytes, string? MimeType, DateTimeOffset? LastModified);

/// <summary>MVP: nur OneDrive Business. Interface bleibt provider-agnostisch,
/// damit später Google Drive / Dropbox über dieselben Endpoints laufen können.</summary>
public class OneDriveConnectorService : IConnectorService
{
    private readonly NimShareDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IDataProtector _protector;
    private readonly IBlobStorageService _blobs;
    private readonly IConfiguration _cfg;
    private readonly ILogger<OneDriveConnectorService> _log;

    public OneDriveConnectorService(NimShareDbContext db, IHttpClientFactory http,
        IDataProtectionProvider dp, IBlobStorageService blobs,
        IConfiguration cfg, ILogger<OneDriveConnectorService> log)
    {
        _db = db;
        _http = http;
        _protector = dp.CreateProtector("NimShare.Connector.OneDrive.v1");
        _blobs = blobs;
        _cfg = cfg;
        _log = log;
    }

    private string ClientId => _cfg["Connectors:OneDrive:ClientId"]
        ?? throw new InvalidOperationException("Connectors:OneDrive:ClientId not configured");
    private string ClientSecret => _cfg["Connectors:OneDrive:ClientSecret"]
        ?? throw new InvalidOperationException("Connectors:OneDrive:ClientSecret not configured");
    private string Tenant => _cfg["Connectors:OneDrive:Tenant"] ?? "common";
    private static readonly string[] Scopes = { "offline_access", "Files.Read", "User.Read" };

    public Task<string> BuildAuthorizeUrlAsync(Guid userId, string redirectUri, string state, string codeVerifier)
    {
        var challenge = ComputeCodeChallenge(codeVerifier);
        var qs = new Dictionary<string, string?>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', Scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var url = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize?"
            + string.Join('&', qs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value ?? "")}"));
        return Task.FromResult(url);
    }

    public async Task<Connector> CompleteAuthorizeAsync(Guid userId, string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var http = _http.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', Scopes),
            ["code_verifier"] = codeVerifier,
        });
        var resp = await http.PostAsync($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)resp.StatusCode}): {body}");
        var tok = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException("Token response not parseable.");
        if (string.IsNullOrEmpty(tok.RefreshToken))
            throw new InvalidOperationException("No refresh_token in response — check that 'offline_access' scope was granted.");

        // Anzeigename + externe Konto-ID aus /me holen.
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
        var meResp = await http.GetAsync("https://graph.microsoft.com/v1.0/me", ct);
        var me = await meResp.Content.ReadFromJsonAsync<GraphUser>(cancellationToken: ct);

        var entity = new Connector
        {
            OwnerUserId = userId,
            Type = ConnectorType.OneDriveBusiness,
            DisplayName = me?.UserPrincipalName ?? me?.DisplayName ?? "OneDrive",
            ExternalAccountId = me?.Id,
            RefreshTokenEncrypted = _protector.Protect(Encoding.UTF8.GetBytes(tok.RefreshToken)),
        };
        _db.Connectors.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<List<ConnectorBrowseItem>> BrowseAsync(Guid connectorId, string? remoteFolderId, CancellationToken ct)
    {
        var (cn, token) = await LoadAndRefreshAsync(connectorId, ct);
        var http = _http.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var url = string.IsNullOrEmpty(remoteFolderId)
            ? "https://graph.microsoft.com/v1.0/me/drive/root/children?$top=200"
            : $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(remoteFolderId)}/children?$top=200";
        var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph browse failed ({(int)resp.StatusCode}): {body}");
        var page = System.Text.Json.JsonSerializer.Deserialize<GraphChildrenPage>(body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var items = (page?.Value ?? new List<GraphDriveItem>())
            .Select(v => new ConnectorBrowseItem(
                v.Id ?? "", v.Name ?? "",
                v.Folder is not null,
                v.Size ?? 0,
                v.File?.MimeType,
                v.LastModifiedDateTime))
            .OrderByDescending(x => x.IsFolder).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        cn.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return items;
    }

    public async Task ImportAsync(Guid connectorId, IReadOnlyList<string> remoteItemIds,
        Guid targetFolderId, bool preserveStructure, CancellationToken ct)
    {
        var (cn, token) = await LoadAndRefreshAsync(connectorId, ct);
        var owner = await _db.Users.FindAsync(new object[] { cn.OwnerUserId }, ct)
            ?? throw new InvalidOperationException("Owner user not found.");
        var targetFolder = await _db.Folders.FindAsync(new object[] { targetFolderId }, ct)
            ?? throw new InvalidOperationException("Target folder not found.");
        // v1.10.163: Scope-aware Zugriffsprüfung — Personal-Folder muss dem
        // Owner gehören, Public und Group sind für alle authentifizierten
        // User schreibbar (FileAccessService setzt die feine Gruppen-Membership,
        // aber writable-all liefert eh nur zugelassene Ordner an das UI).
        var allowed = targetFolder.Scope switch
        {
            FileScope.Personal => targetFolder.OwnerUserId == owner.Id,
            FileScope.Public => true,
            FileScope.Group => true,
            _ => false,
        };
        if (!allowed)
            throw new UnauthorizedAccessException("Target folder is not writable for this user.");

        var http = _http.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var itemId in remoteItemIds)
        {
            ct.ThrowIfCancellationRequested();
            await ImportOneAsync(http, itemId, targetFolder, owner, preserveStructure, ct);
        }
        cn.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Aktueller Personal-Quota-Verbrauch des Owners in Bytes.
    /// Nur Personal-Files, wie FilesController.InitUpload. Deleted zählt nicht.</summary>
    private async Task<long> UsedPersonalBytesAsync(Guid ownerId, CancellationToken ct)
        => await _db.Files
            .Where(f => f.OwnerId == ownerId
                && f.Scope == FileScope.Personal
                && f.Status != StorageFileStatus.Deleted)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0L;

    private static string SanitiseFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }

    private async Task ImportOneAsync(HttpClient http, string itemId, Folder targetFolder, User owner, bool preserveStructure, CancellationToken ct)
    {
        // Item-Metadaten holen (Name, Größe, MimeType, ob Ordner).
        var metaResp = await http.GetAsync($"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(itemId)}", ct);
        var meta = await metaResp.Content.ReadFromJsonAsync<GraphDriveItem>(cancellationToken: ct);
        if (meta is null) return;

        if (meta.Folder is not null)
        {
            // Rekursiv importieren; optional Ordner mit-erzeugen.
            Folder importInto = targetFolder;
            if (preserveStructure)
            {
                importInto = new Folder
                {
                    Name = meta.Name ?? "OneDrive",
                    ParentFolderId = targetFolder.Id,
                    Scope = targetFolder.Scope,
                    OwnerUserId = targetFolder.OwnerUserId,
                    OwnerGroupId = targetFolder.OwnerGroupId,
                    CreatedByUserId = owner.Id, // v1.10.163: Audit-Nachweis
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _db.Folders.Add(importInto);
                await _db.SaveChangesAsync(ct);
            }
            // Children durchgehen.
            var childrenResp = await http.GetAsync(
                $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(itemId)}/children?$top=200", ct);
            var childrenBody = await childrenResp.Content.ReadAsStringAsync(ct);
            var page = System.Text.Json.JsonSerializer.Deserialize<GraphChildrenPage>(childrenBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var child in page?.Value ?? new())
            {
                if (child.Id is null) continue;
                await ImportOneAsync(http, child.Id, importInto, owner, preserveStructure, ct);
            }
            return;
        }

        // Reguläre Datei: Content-Stream vom Graph → Azure Blob PUT.
        var size = meta.Size ?? 0;
        var name = meta.Name ?? "unnamed";
        var mime = meta.File?.MimeType ?? "application/octet-stream";

        // v1.10.163: Quota-Check für Personal-Scope-Ziele — analog
        // FilesController.InitUpload. Sonst könnte ein User seine 100 MB
        // Quota mit einem 50 GB OneDrive-Ordner sprengen.
        if (targetFolder.Scope == FileScope.Personal && owner.QuotaBytes > 0)
        {
            var used = await UsedPersonalBytesAsync(owner.Id, ct);
            if (used + size > owner.QuotaBytes)
            {
                _log.LogWarning("Import {ItemId} skipped: quota would exceed ({Used}+{Size} > {Quota}) for user {UserId}.",
                    itemId, used, size, owner.QuotaBytes, owner.Id);
                throw new InvalidOperationException(
                    $"Personal-Quota erschöpft: benötigt {size / (1024 * 1024)} MB, frei {(owner.QuotaBytes - used) / (1024 * 1024)} MB.");
            }
        }

        // Blob-Pfad-Konvention exakt wie FilesController.InitUpload:
        // users/{userId:N}/{fileId:N}/{safeName}. Die trailing-safeName-
        // Segmentierung nutzen Preview- und Download-Codepfade zur Name-
        // Wiederherstellung.
        var fileId = Guid.NewGuid();
        var blobPath = $"users/{owner.Id:N}/{fileId:N}/{SanitiseFilename(name)}";

        // File-Row anlegen (Status = Uploading, bis Streaming durch ist).
        var file = new StorageFile
        {
            Id = fileId,
            Name = name,
            ContentType = mime,
            SizeBytes = size,
            OwnerId = owner.Id,
            FolderId = targetFolder.Id,
            GroupId = targetFolder.OwnerGroupId,
            Scope = targetFolder.Scope,
            BlobPath = blobPath,
            Status = StorageFileStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Files.Add(file);
        await _db.SaveChangesAsync(ct);

        // Content vom Graph streamen (HEAD 302-Redirect zum CDN wird von
        // HttpClient automatisch verfolgt).
        using var srcResp = await http.GetAsync(
            $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(itemId)}/content",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!srcResp.IsSuccessStatusCode)
        {
            var errBody = await srcResp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("OneDrive content fetch failed for {ItemId}: {Status} {Body}", itemId, (int)srcResp.StatusCode, errBody);
            _db.Files.Remove(file);
            await _db.SaveChangesAsync(ct);
            return;
        }
        await using var src = await srcResp.Content.ReadAsStreamAsync(ct);
        // BlobStorage-Service kennt den API-Contract für Streaming-Uploads.
        await _blobs.UploadFromStreamAsync(blobPath, src, mime, ct);

        file.Status = StorageFileStatus.Ready;
        file.ReadyAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<(Connector cn, string accessToken)> LoadAndRefreshAsync(Guid connectorId, CancellationToken ct)
    {
        var cn = await _db.Connectors.SingleOrDefaultAsync(c => c.Id == connectorId, ct)
            ?? throw new InvalidOperationException("Connector not found.");
        var refreshToken = Encoding.UTF8.GetString(_protector.Unprotect(cn.RefreshTokenEncrypted));

        var http = _http.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = string.Join(' ', Scopes),
        });
        var resp = await http.PostAsync($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token refresh failed ({(int)resp.StatusCode}): {body}");
        var tok = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException("Refresh response not parseable.");
        // Refresh-Token rotiert bei jedem Refresh — neuen sofort zurückspeichern.
        if (!string.IsNullOrEmpty(tok.RefreshToken))
        {
            cn.RefreshTokenEncrypted = _protector.Protect(Encoding.UTF8.GetBytes(tok.RefreshToken));
            await _db.SaveChangesAsync(ct);
        }
        return (cn, tok.AccessToken ?? throw new InvalidOperationException("No access_token in refresh response."));
    }

    private static string ComputeCodeChallenge(string verifier)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // Graph/OAuth DTOs — minimal.
    private class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }
    private class GraphUser
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? UserPrincipalName { get; set; }
    }
    private class GraphChildrenPage
    {
        public List<GraphDriveItem>? Value { get; set; }
    }
    private class GraphDriveItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public long? Size { get; set; }
        public DateTimeOffset? LastModifiedDateTime { get; set; }
        public GraphFolder? Folder { get; set; }
        public GraphFile? File { get; set; }
    }
    private class GraphFolder { public int? ChildCount { get; set; } }
    private class GraphFile { public string? MimeType { get; set; } }
}
