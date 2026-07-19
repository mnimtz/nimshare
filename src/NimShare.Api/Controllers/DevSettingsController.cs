using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
[Route("settings/dev")]
public class DevSettingsController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View("Index");
}
