using System.Globalization;
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

    /// <summary>
    /// The id of the session that a user is signed in with, or null when they are not signed in.
    /// It names the session, and is not its token: whoever knows it is not signed in by it.
    /// </summary>
    /// <param name="user">The user of a request, or of the connection of an interactive page.</param>
    public static Guid? SessionIdOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity is { IsAuthenticated: true }
            && Guid.TryParse(user.FindFirstValue(SessionAuthentication.SessionClaimType), out var sessionId)
            ? sessionId
            : null;
    }

    /// <summary>
    /// The theme of the account that a user is signed in to, or null when they are not signed
    /// in: how the pages look to them on every device (FR-THM-3). It is what the account said
    /// when the request was signed in, which is on every request.
    /// </summary>
    /// <param name="user">The user of a request.</param>
    public static Theme? ThemeOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity is { IsAuthenticated: true }
            && Enum.TryParse<Theme>(user.FindFirstValue(SessionAuthentication.ThemeClaimType), out var theme)
            && Enum.IsDefined(theme)
            ? theme
            : null;
    }

    /// <summary>
    /// How much of the pages moves for the account that a user is signed in to, or null when
    /// they are not signed in (FR-ANI-4). It is what the account said when the request was
    /// signed in, which is on every request.
    /// </summary>
    /// <param name="user">The user of a request.</param>
    public static Motion? MotionOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity is { IsAuthenticated: true }
            && Enum.TryParse<Motion>(user.FindFirstValue(SessionAuthentication.MotionClaimType), out var motion)
            && Enum.IsDefined(motion)
            ? motion
            : null;
    }

    /// <summary>
    /// When the session that a user is signed in with ends if nothing more is done with it, or
    /// null when they are not signed in. It is what the session said when the request was
    /// signed in, which is on every request: what another window of the same browser did since
    /// is not in it.
    /// </summary>
    /// <param name="user">The user of a request.</param>
    public static DateTimeOffset? SessionEndsOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity is { IsAuthenticated: true }
            && DateTimeOffset.TryParseExact(
                user.FindFirstValue(SessionAuthentication.EndsClaimType),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var endsAt)
            ? endsAt
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
