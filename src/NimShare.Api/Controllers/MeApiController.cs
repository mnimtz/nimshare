using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.10.82: /api/v1/me — Endpoints rund um den eigenen Account.
///
/// Der DELETE ist Apple-Guideline-5.1.1(v)-Blocker: seit Juni 2022
/// muss jede App mit Account-Erstellung auch das Löschen IN-APP
/// anbieten. Ohne diesen Endpoint gibt es kein Store-Approval.
/// </summary>
[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = "ApiUser")]
public class MeApiController : ControllerBase
{
    private readonly ICurrentUserService _current;
    private readonly IPasswordHasher _hasher;
    private readonly IUserDeletionService _deleter;
    private readonly ILogger<MeApiController> _log;

    public MeApiController(ICurrentUserService current, IPasswordHasher hasher,
        IUserDeletionService deleter, ILogger<MeApiController> log)
    {
        _current = current; _hasher = hasher; _deleter = deleter; _log = log;
    }

    public record DeleteAccountRequest(string? Password);

    /// <summary>
    /// Löscht den eigenen Account inklusive aller Daten. Passwort wird
    /// verlangt (kein Zufalls-Klick durch fremdes Gerät ohne Confirmation).
    /// SSO/Entra-User können ohne Passwort löschen, wenn der Account
    /// keins gesetzt hat — dann reicht die Authentifizierung durch den
    /// gültigen JWT.
    /// </summary>
    [HttpDelete("")]
    public async Task<IActionResult> DeleteMe([FromBody] DeleteAccountRequest req, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);

        // Passwort-Check nur wenn der User überhaupt ein lokales Passwort hat.
        if (!string.IsNullOrEmpty(me.PasswordHash))
        {
            if (string.IsNullOrEmpty(req?.Password) || !_hasher.Verify(req.Password, me.PasswordHash))
            {
                return Problem(statusCode: 401, title: "Passwort falsch",
                    detail: "Bitte aktuelles Passwort eingeben, um die Löschung zu bestätigen.");
            }
        }

        // Admin-Safety: der letzte Admin darf sich nicht selbst löschen.
        // Sonst hätte die Instanz keinen Admin mehr und niemand kann sie
        // konfigurieren.
        if (me.Role == UserRole.Admin)
        {
            var otherAdminsCount = HttpContext.RequestServices
                .GetRequiredService<NimShare.Core.Data.NimShareDbContext>()
                .Users.Count(u => u.Role == UserRole.Admin && u.Id != me.Id && u.IsActive);
            if (otherAdminsCount == 0)
            {
                return Problem(statusCode: 409, title: "Letzter Admin",
                    detail: "Du bist der letzte aktive Admin. Vor der Löschung bitte einen anderen User zum Admin machen.");
            }
        }

        _log.LogWarning("User {Id} ({Email}) fordert Account-Löschung an", me.Id, me.Email);
        var result = await _deleter.DeleteAsync(me.Id, ct);
        return Ok(new
        {
            deleted = true,
            filesRemoved = result.FilesDeleted,
            bytesFreed = result.BytesFreed,
            blobDeleteFailures = result.BlobDeleteFailures,
        });
    }
}
