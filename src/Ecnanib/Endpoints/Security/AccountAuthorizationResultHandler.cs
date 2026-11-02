using Ecnanib.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.Extensions;

namespace Ecnanib.Endpoints.Security;

/// <summary>
/// A user with a document to accept is forbidden by the default policy, and that needs to send them to
/// accept it rather than to an error. Components/Account/AccessGate.razor is the same rule on a circuit.
/// </summary>
internal sealed class AccountAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<LegalAcceptedRequirement>().Any() != true)
        {
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }

        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;

        context.Response.Redirect("/legal/accept" + QueryString.Create("returnUrl", returnUrl));

        return Task.CompletedTask;
    }
}
