using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class AiChatController : Controller
{
    private readonly IAiGatewayService _ai;
    private readonly ICurrentUserService _users;

    public AiChatController(IAiGatewayService ai, ICurrentUserService users)
    {
        _ai = ai;
        _users = users;
    }

    [HttpGet("/ai/chat")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // v2.0-web: Bereichs-Auswahl auf Persönlich/Öffentlich reduziert —
        // Marcus's Korrektur: Gruppen sind seit v1.10.102 keine durchsuchbare
        // Bibliothek mehr, nur noch Verteiler-Namen für Direct-Shares, gehören
        // also nicht in diesen Picker.
        await _users.GetOrProvisionAsync(User, ct);
        var settings = await _ai.LoadAsync(ct);
        ViewData["Enabled"] = settings.EnableChatWithFiles && settings.Provider != AiProvider.Disabled;
        return View();
    }
}
