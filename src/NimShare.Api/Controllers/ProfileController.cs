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

    public ProfileController(NimShareDbContext db, ICurrentUserService users, IPasswordHasher hasher)
    {
        _db = db;
        _users = users;
        _hasher = hasher;
    }

    public record ProfileFormModel(string DisplayName,
        string? CurrentPassword, string? NewPassword, string? NewPasswordConfirm);

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

        // Optional password change: only apply if all three password fields are filled.
        if (!string.IsNullOrEmpty(form.NewPassword) || !string.IsNullOrEmpty(form.CurrentPassword))
        {
            if (string.IsNullOrEmpty(me.PasswordHash))
            {
                TempData["Error"] = "This account uses Entra sign-in only; there is no local password to change.";
                return RedirectToAction(nameof(Index));
            }
            if (string.IsNullOrEmpty(form.CurrentPassword) || !_hasher.Verify(form.CurrentPassword, me.PasswordHash))
            {
                TempData["Error"] = "Current password is wrong.";
                return RedirectToAction(nameof(Index));
            }
            if (form.NewPassword != form.NewPasswordConfirm)
            {
                TempData["Error"] = "New passwords do not match.";
                return RedirectToAction(nameof(Index));
            }
            if ((form.NewPassword ?? "").Length < 8)
            {
                TempData["Error"] = "New password must be at least 8 characters.";
                return RedirectToAction(nameof(Index));
            }
            me.PasswordHash = _hasher.Hash(form.NewPassword!);
        }

        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = "Profile saved.";
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
            TempData["Error"] = "No image received.";
            return RedirectToAction(nameof(Index));
        }
        if (file.Length > 3 * 1024 * 1024)
        {
            TempData["Error"] = "Avatar image is too large (max 3 MiB after cropping).";
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
                TempData["Error"] = "Upload failed. Try again.";
                return RedirectToAction(nameof(Index));
            }
        }
        me.AvatarBlobPath = path;
        me.AvatarUrl = $"/avatars/{me.Id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = "Avatar updated.";
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
        TempData["Notice"] = "Avatar removed.";
        return RedirectToAction(nameof(Index));
    }
}
