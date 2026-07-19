using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NimShare.Api.Services;

/// <summary>
/// Global fallback: a scoped API token that lacks any write/manage/wildcard
/// scope may only invoke safe HTTP verbs. Prevents "files:read"-scoped tokens
/// from POST/PUT/DELETEing anywhere until per-endpoint RequireScope attributes
/// grow in.
/// </summary>
public class ApiTokenMethodGuard : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!ApiTokenScope.AllowsMethod(context.HttpContext.User, context.HttpContext.Request.Method))
        {
            context.Result = new ObjectResult(new
            {
                error = "insufficient_scope",
                message = "This API token is read-only.",
            })
            { StatusCode = 403 };
            return Task.CompletedTask;
        }
        return next();
    }
}
