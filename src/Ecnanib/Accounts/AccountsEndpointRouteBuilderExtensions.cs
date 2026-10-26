using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Microsoft.AspNetCore.Antiforgery;

namespace Ecnanib.Accounts;

public static class AccountsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST /sign-out</c> (FR-ACC-02). The request must carry a valid antiforgery token (NFR-SEC-01), which the
    /// layout's sign-out form renders; a minimal endpoint without form parameters is not validated by the middleware, so
    /// the token is checked here. The session is ended on the server too: the user's security stamp is rotated, so a
    /// copied cookie is rejected at its next check, and the cookie is deleted whatever the database said. A GET can never
    /// sign anyone out. No authorization requirement: an expired session pressing "Sign out" lands on the landing page
    /// rather than in the sign-in challenge.
    /// </summary>
    public static IEndpointConventionBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost("/sign-out", async (HttpContext http, IAntiforgery antiforgery, IAccountService accounts, ILoggerFactory loggers) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false))
                return Results.BadRequest();

            if (http.User.GetUserId() is { } userId)
            {
                try
                {
                    await accounts.SignOutAsync(userId, http.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The cookie still goes; the other sessions end at their next revalidation instead of at once.
                    loggers.CreateLogger(typeof(AccountSession).FullName!).LogWarning(ex, "User {UserId} signed out, but the stamp rotation failed; other sessions end at their next check", userId);
                }
            }

            await AccountSession.SignOutAsync(http).ConfigureAwait(false);
            return Results.LocalRedirect("/");
        });
    }
}
