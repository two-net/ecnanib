using System.Security.Claims;

namespace Ecnanib.Accounts.Abstractions;

/// <summary>
/// Reads the signed-in user from a principal. Every data access is scoped to this id (NFR-SEC-03); nothing else
/// identifies a user to the rest of the application.
/// </summary>
public static class AccountClaims
{
    /// <summary>Claim carrying <c>users.security_stamp</c> at sign-in time; a mismatch ends the session (FR-ACC-03).</summary>
    public const string SecurityStamp = "ecnanib:security-stamp";

    public static Guid? GetUserId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : null;
    }

    public static Guid RequireUserId(this ClaimsPrincipal? principal) =>
        principal.GetUserId() ?? throw new InvalidOperationException("No signed-in user.");

    public static string? GetSecurityStamp(this ClaimsPrincipal? principal) =>
        principal?.FindFirst(SecurityStamp)?.Value;
}
