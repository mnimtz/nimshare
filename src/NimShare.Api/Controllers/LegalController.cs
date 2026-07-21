using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.10.88: App-Store Round 3 — öffentliche Privacy- und Support-Seite.
/// Apple verlangt beide URLs als Store-Metadaten. Wir hosten sie direkt
/// im NimShare-Backend statt auf externem Static-Host, damit sie sich
/// mit der Instanz mitversionieren und der User immer die passende
/// Policy zu seiner selbst-gehosteten Instanz sieht.
///
/// Beide Routen sind AllowAnonymous — Apple's Review-Bot muss sie
/// erreichen können ohne Login.
/// </summary>
[AllowAnonymous]
public class LegalController : Controller
{
    [HttpGet("/privacy")]
    public IActionResult Privacy() => View();

    [HttpGet("/support")]
    public IActionResult Support() => View();

    [HttpGet("/imprint")]
    public IActionResult Imprint() => View();
}
