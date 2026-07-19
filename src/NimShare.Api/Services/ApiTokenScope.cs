using System.Security.Claims;

namespace NimShare.Api.Services;

/// <summary>
/// Scope check for personal API tokens. When a caller authenticated via
/// <see cref="ApiTokenAuthHandler"/>, the emitted principal carries every scope
/// as a "nimshare.scope" claim. Cookie / JWT / Entra sessions have no scope
/// claim at all — they represent the full user and pass every check.
/// </summary>
public static class ApiTokenScope
{
    public const string ClaimType = "nimshare.scope";

    /// <summary>True if the caller is allowed to invoke an action needing the
    /// given scope. Cookie/JWT/Entra users always pass; scoped API tokens must
    /// list the scope OR a wildcard "*".</summary>
    public static bool Allows(ClaimsPrincipal principal, string requiredScope)
    {
        // No scope claim at all → not a scoped token → full rights.
        if (!principal.HasClaim(c => c.Type == ClaimType)) return true;
        foreach (var c in principal.FindAll(ClaimType))
        {
            if (c.Value == "*" || c.Value == requiredScope) return true;
            // Allow "files:*" to cover "files:read" and "files:write".
            var colon = requiredScope.IndexOf(':');
            if (colon > 0 && c.Value == requiredScope[..colon] + ":*") return true;
        }
        return false;
    }

    /// <summary>Coarse-grained fallback check for endpoints without an explicit
    /// RequireScope attribute: any state-changing HTTP verb needs a "write" or
    /// wildcard scope. Applied globally so a token with scope "files:read"
    /// cannot POST/PUT/DELETE anywhere.</summary>
    public static bool AllowsMethod(ClaimsPrincipal principal, string method)
    {
        if (!principal.HasClaim(c => c.Type == ClaimType)) return true;
        var isSafe = method is "GET" or "HEAD" or "OPTIONS";
        if (isSafe) return true; // reads always allowed once authenticated
        foreach (var c in principal.FindAll(ClaimType))
        {
            if (c.Value == "*") return true;
            if (c.Value.EndsWith(":write", StringComparison.OrdinalIgnoreCase)) return true;
            if (c.Value.EndsWith(":manage", StringComparison.OrdinalIgnoreCase)) return true;
            if (c.Value.EndsWith(":*", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
