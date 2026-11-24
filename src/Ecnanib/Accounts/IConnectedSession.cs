using System.Security.Claims;

namespace Ecnanib.Accounts;

/// <summary>
/// The session of a connection: what a page that follows what changes asks about the session
/// that its connection was made with (FR-NTF-2, NFR-SEC-1). See "The session of a connection"
/// in docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// A page that is rendered for each request is checked with each request. A page that follows
/// keeps a connection open, whose user is decided when the connection is made and is not asked
/// for again. So what writes to the page over the connection asks here, before it goes on,
/// whether the session still stands.
/// </para>
/// <para>
/// <b>What such a page does by itself is no activity of its user.</b> A session ends after a
/// period of inactivity, and a page that kept it alive would keep a browser signed in that
/// nobody sits at. The connection of the page is no activity, and neither is the request with
/// which the page asks for itself again, when the page said here that it would.
/// </para>
/// </remarks>
public interface IConnectedSession
{
    /// <summary>
    /// Whether the session that a connection was made with still stands: it was not ended, by
    /// signing out, by a new password, or with the account, and it is not past its period of
    /// inactivity. Asking is no activity.
    /// </summary>
    /// <param name="user">The user of the connection, as it was signed in when it was made.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>False also for a user who is not signed in, or who names no session.</returns>
    Task<bool> StandsAsync(ClaimsPrincipal user, CancellationToken cancellationToken);

    /// <summary>
    /// Says that a page is about to ask for itself again, by itself: the request of its session
    /// for that address that follows within seconds is no activity of its user. It is good for
    /// one request, and each page that says so is counted.
    /// </summary>
    /// <param name="user">The user of the connection of the page.</param>
    /// <param name="address">The path and the query that the page is asked for at, as the browser sends them.</param>
    void ExpectsPageAgain(ClaimsPrincipal user, string address);
}

/// <summary>Answers for the sessions in the database. See <see cref="IConnectedSession"/>.</summary>
internal sealed class ConnectedSession(Sessions sessions) : IConnectedSession
{
    public async Task<bool> StandsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return SignedInUser.IdOf(user) is { } userId
            && SignedInUser.SessionIdOf(user) is { } sessionId
            && await sessions.StandsAsync(sessionId, userId, cancellationToken);
    }

    public void ExpectsPageAgain(ClaimsPrincipal user, string address)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(address);

        if (SignedInUser.SessionIdOf(user) is { } sessionId)
        {
            sessions.ExpectPageAgain(sessionId, address);
        }
    }
}
