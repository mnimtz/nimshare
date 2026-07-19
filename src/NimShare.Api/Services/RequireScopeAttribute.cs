using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NimShare.Api.Services;

/// <summary>Deny scoped API tokens that don't list the required scope. Applied
/// as a filter on write endpoints. Cookie/JWT sessions pass through.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class RequireScopeAttribute : Attribute, IAsyncAuthorizationFilter
{
    public string Scope { get; }
    public RequireScopeAttribute(string scope) { Scope = scope; }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!ApiTokenScope.Allows(context.HttpContext.User, Scope))
        {
            context.Result = new ObjectResult(new
            {
                error = "insufficient_scope",
                required = Scope,
                message = "This API token lacks the required scope.",
            })
            { StatusCode = 403 };
        }
        return Task.CompletedTask;
    }
}
