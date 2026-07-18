using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class AiGatewayController : Controller
{
    private readonly IAiGatewayService _ai;
    private readonly ICurrentUserService _users;

    public AiGatewayController(IAiGatewayService ai, ICurrentUserService users)
    {
        _ai = ai;
        _users = users;
    }

    private async Task<bool> IsAdmin(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return me.Role == UserRole.Admin;
    }

    [HttpGet("/settings/ai")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var s = await _ai.LoadAsync(ct);
        return View(s);
    }

    public record SaveForm(AiProvider Provider, string? Model, string? Endpoint, string? ApiKey,
        bool EnableAutoSummary, bool EnableSmartTags, bool EnableSemanticSearch,
        bool EnableGuidedUploadRequests, bool EnableContentRiskDetection,
        bool EnableDraftedShareEmails, bool EnableChatWithFiles, bool EnableOcr);

    [HttpPost("/settings/ai")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromForm] SaveForm form, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var me = await _users.GetOrProvisionAsync(User, ct);
        var incoming = new AiGatewaySettings
        {
            Provider = form.Provider,
            Model = form.Model,
            Endpoint = form.Endpoint,
            EnableAutoSummary = form.EnableAutoSummary,
            EnableSmartTags = form.EnableSmartTags,
            EnableSemanticSearch = form.EnableSemanticSearch,
            EnableGuidedUploadRequests = form.EnableGuidedUploadRequests,
            EnableContentRiskDetection = form.EnableContentRiskDetection,
            EnableDraftedShareEmails = form.EnableDraftedShareEmails,
            EnableChatWithFiles = form.EnableChatWithFiles,
            EnableOcr = form.EnableOcr,
        };
        await _ai.SaveAsync(incoming, form.ApiKey, me.Id, ct);
        TempData["Notice"] = "AI gateway saved.";
        return RedirectToAction(nameof(Index));
    }
}
