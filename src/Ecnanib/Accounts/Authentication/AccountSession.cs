using System.Security.Claims;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Database.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Ecnanib.Accounts.Authentication;

/// <summary>
/// The cookie session (FR-ACC-02): one persistent, sliding cookie whose ticket carries the user id, email and security
/// stamp and is protected by the Data Protection key ring the database module persists (FR-CFG-05). Every sign-in mints
/// a fresh ticket, so nothing from before authentication is carried forward (NFR-SEC-01).
/// </summary>
internal static class AccountSession
{
    public const string CookieName = "ecnanib.session";

    public const string AntiforgeryCookieName = "ecnanib.antiforgery";

    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static ClaimsPrincipal CreatePrincipal(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new Claim(ClaimTypes.Name, user.Email),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(AccountClaims.SecurityStamp, user.SecurityStamp),
            ],
            Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Issues (or re-issues, after a password change) the session cookie for the current response.</summary>
    public static Task SignInAsync(HttpContext httpContext, User user)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        // IssuedUtc and ExpiresUtc are stamped by the handler; the revalidation interval is measured from IssuedUtc.
        return httpContext.SignInAsync(Scheme, CreatePrincipal(user), new AuthenticationProperties { IsPersistent = true });
    }

    public static Task SignOutAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.SignOutAsync(Scheme);
    }
}
