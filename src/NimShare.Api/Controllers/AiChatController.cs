using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class AiChatController : Controller
{
    private readonly IAiGatewayService _ai;
    private readonly IFileAccessService _access;
    private readonly ICurrentUserService _users;

    public AiChatController(IAiGatewayService ai, IFileAccessService access, ICurrentUserService users)
    {
        _ai = ai;
        _access = access;
        _users = users;
    }

    [HttpGet("/ai/chat")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var settings = await _ai.LoadAsync(ct);
        ViewData["Enabled"] = settings.EnableChatWithFiles && settings.Provider != AiProvider.Disabled;
        ViewData["Groups"] = await _access.ListMyGroupsAsync(me, ct);
        return View();
    }
}
