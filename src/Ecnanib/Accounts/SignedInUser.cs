using System.Security.Claims;

namespace Ecnanib.Accounts;

/// <summary>
/// Who a request is signed in as: the user whose data it may ask for (NFR-SEC-3). Whatever serves
/// a request takes the user from here, and never from the request's own words: not from a path,
/// a query, a form, or a body. See docs/ownership.md.
/// </summary>
public static class SignedInUser
{
    /// <summary>The id of the account a user is signed in to, or null when they are not signed in.</summary>
    /// <param name="user">The user of a request, or of the connection of an interactive page.</param>
    public static Guid? IdOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity is { IsAuthenticated: true }
            && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : null;
    }

    /// <summary>The id of the account a request is signed in to, or null when it is not signed in.</summary>
    public static Guid? IdOf(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IdOf(request.User);
    }

    /// <summary>
    /// The id of the account a request is signed in to, for what only a signed-in user reaches.
    /// It fails for a request that is not signed in.
    /// </summary>
    public static Guid RequiredIdOf(HttpContext request) =>
        IdOf(request) ?? throw new InvalidOperationException("This is for a signed-in user, and the request has none.");
}
