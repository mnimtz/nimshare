using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class ProfileController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IPasswordHasher _hasher;
    private readonly IStringLocalizer<SharedResources> _l;

    public ProfileController(NimShareDbContext db, ICurrentUserService users, IPasswordHasher hasher, IStringLocalizer<SharedResources> l)
    {
        _db = db;
        _users = users;
        _hasher = hasher;
        _l = l;
    }

    public record ProfileFormModel(string DisplayName,
        string? CurrentPassword, string? NewPassword, string? NewPasswordConfirm,
        bool ShowAvatarOnLandings = false);

    [HttpGet("/settings/profile")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return View(me);
    }

    [HttpPost("/settings/profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update([FromForm] ProfileFormModel form, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!string.IsNullOrWhiteSpace(form.DisplayName))
            me.DisplayName = form.DisplayName.Trim();
        // Landing-Avatar opt-in — always overwritten (checkbox POSTs "on" or
        // is absent; the bool binds true/false accordingly).
        me.ShowAvatarOnLandings = form.ShowAvatarOnLandings;

        // Optional password change: only apply if all three password fields are filled.
        if (!string.IsNullOrEmpty(form.NewPassword) || !string.IsNullOrEmpty(form.CurrentPassword))
        {
            if (string.IsNullOrEmpty(me.PasswordHash))
            {
                TempData["Error"] = _l["err.entra_only"].Value;
                return RedirectToAction(nameof(Index));
            }
            if (string.IsNullOrEmpty(form.CurrentPassword) || !_hasher.Verify(form.CurrentPassword, me.PasswordHash))
            {
                TempData["Error"] = _l["err.wrong_password"].Value;
                return RedirectToAction(nameof(Index));
            }
            if (form.NewPassword != form.NewPasswordConfirm)
            {
                TempData["Error"] = _l["err.password_mismatch"].Value;
                return RedirectToAction(nameof(Index));
            }
            if ((form.NewPassword ?? "").Length < 8)
            {
                TempData["Error"] = _l["err.password_too_short"].Value;
                return RedirectToAction(nameof(Index));
            }
            me.PasswordHash = _hasher.Hash(form.NewPassword!);
        }

        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = _l["notice.profile_saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    // ── Avatar upload: multipart with a client-cropped PNG (256×256) ───────

    [HttpPost("/settings/profile/avatar")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(4 * 1024 * 1024)] // 4 MiB is more than enough for a 256px PNG
    public async Task<IActionResult> UploadAvatar([FromForm] IFormFile file,
        [FromServices] IBlobStorageService blobs, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = _l["err.no_image"].Value;
            return RedirectToAction(nameof(Index));
        }
        if (file.Length > 3 * 1024 * 1024)
        {
            TempData["Error"] = _l["err.image_too_large"].Value;
            return RedirectToAction(nameof(Index));
        }
        // Store as a single fixed blob path per user; overwrite on re-upload.
        var path = $"users/{me.Id:N}/avatar.png";
        var ticket = blobs.CreateUploadTicket(path);
        // Upload from the server (simpler than another round-trip to Blob).
        using (var http = new HttpClient())
        using (var content = new StreamContent(file.OpenReadStream()))
        {
            content.Headers.Add("x-ms-blob-type", "BlockBlob");
            content.Headers.Add("x-ms-blob-content-type", file.ContentType ?? "image/png");
            var resp = await http.PutAsync(ticket.UploadUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                TempData["Error"] = _l["err.upload_failed"].Value;
                return RedirectToAction(nameof(Index));
            }
        }
        me.AvatarBlobPath = path;
        me.AvatarUrl = $"/avatars/{me.Id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = _l["notice.avatar_updated"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/profile/avatar/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAvatar([FromServices] IBlobStorageService blobs, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!string.IsNullOrEmpty(me.AvatarBlobPath))
        {
            try { await blobs.DeleteAsync(me.AvatarBlobPath, ct); } catch { /* best effort */ }
        }
        me.AvatarBlobPath = null;
        me.AvatarUrl = null;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = _l["notice.avatar_removed"].Value;
        return RedirectToAction(nameof(Index));
    }
}
