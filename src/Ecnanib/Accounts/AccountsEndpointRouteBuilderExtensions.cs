using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Microsoft.AspNetCore.Antiforgery;

namespace Ecnanib.Accounts;

public static class AccountsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST /sign-out</c> (FR-ACC-02). The request must carry a valid antiforgery token (NFR-SEC-01), which the
    /// layout's sign-out form renders; a minimal endpoint without form parameters is not validated by the middleware, so
    /// the token is checked here. A GET can never sign anyone out. No authorization requirement: an expired session
    /// pressing "Sign out" lands on the landing page rather than in the sign-in challenge.
    /// </summary>
    public static IEndpointConventionBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost("/sign-out", async (HttpContext http, IAntiforgery antiforgery, ILoggerFactory loggers) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false))
                return Results.BadRequest();

            var userId = http.User.GetUserId();
            await AccountSession.SignOutAsync(http).ConfigureAwait(false);
            if (userId is not null)
                loggers.CreateLogger(typeof(AccountSession).FullName!).LogInformation("User {UserId} signed out", userId);
            return Results.LocalRedirect("/");
        });
    }
}
