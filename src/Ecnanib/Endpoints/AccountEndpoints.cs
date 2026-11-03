using Ecnanib.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Ecnanib.Endpoints;

/// <summary>
/// Sign-out is here because the interactive menu submits it, and a circuit can't clear a cookie. The
/// other account forms post to their own static pages. See docs/architecture.md.
/// </summary>
internal static class AccountEndpoints
{
    public const string SignOut = "/sign-out";

    /// <summary>
    /// The form parameter is what enforces the token: UseAntiforgery only records its result, and the
    /// form reader rejects a bad one. Without it this would pass CsrfTests and check nothing. Anonymous,
    /// so a user with a document to accept can still leave. It revokes the session rather than only
    /// clearing the cookie, which is what closes the user's other tabs.
    /// </summary>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost(SignOut, async (HttpContext context, ISessions sessions, [FromForm] SignOutForm _) =>
            {
                if (AccountClaims.UserId(context.User) is { } userId && AccountClaims.SessionId(context.User) is { } sessionId)
                {
                    await sessions.SignOutAsync(userId, sessionId, context.RequestAborted);
                }

                await context.SignOutAsync();

                return Results.LocalRedirect("/");
            })
            .AllowAnonymous();

        return endpoints;
    }

    /// <summary>Nothing to read: a form's token travels with its other fields.</summary>
    private sealed class SignOutForm;
}
