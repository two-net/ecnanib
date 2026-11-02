using Ecnanib.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Reads the claim rather than the database, because this runs on every page. A new version arrives
/// with a new build, so a restart ends every circuit, and the next request fails this and re-accepts.
/// </summary>
internal sealed class LegalAcceptedHandler : AuthorizationHandler<LegalAcceptedRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LegalAcceptedRequirement requirement)
    {
        if (context.User.HasClaim(AccountClaims.LegalDocuments, LegalDocuments.Current))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class AdministratorRequirement : IAuthorizationRequirement;

internal sealed class AdministratorHandler(IAdministrators administrators) : AuthorizationHandler<AdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdministratorRequirement requirement)
    {
        if (AccountClaims.UserId(context.User) is { } userId && await administrators.IsAdministratorAsync(userId, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}
