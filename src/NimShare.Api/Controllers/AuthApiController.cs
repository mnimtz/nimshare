using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// JSON auth endpoints for mobile clients (iOS / Android). Same lookup path as
/// the Razor login flow — issues a JWT for /api/v1/* usage instead of dropping
/// a cookie.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthApiController : ControllerBase
{
    private readonly ILocalAuthService _auth;
    private readonly IJwtTokenService _jwt;
    private readonly ICurrentUserService _current;

    public AuthApiController(ILocalAuthService auth, IJwtTokenService jwt, ICurrentUserService current)
    {
        _auth = auth;
        _jwt = jwt;
        _current = current;
    }

    public record LoginRequest(string Email, string Password);
    public record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);
    public record UserDto(Guid Id, string Email, string DisplayName, string Role, string? AvatarUrl, long QuotaBytes, string PreferredCulture);

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Email) || string.IsNullOrEmpty(req.Password))
            return BadRequest();
        var user = await _auth.AuthenticateAsync(req.Email, req.Password, ct);
        if (user is null) return Unauthorized();
        var token = _jwt.Issue(user, out var exp);
        return Ok(new LoginResponse(token, exp, ToDto(user)));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        return Ok(ToDto(me));
    }

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.AvatarUrl, u.QuotaBytes, u.PreferredCulture);
}
