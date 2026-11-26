using System.Security.Claims;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Accounts;

/// <summary>
/// When the session of a request ends, and how long before that its user is told so: what a
/// page writes for the notice that a session is about to end (NFR-USA-1, NFR-SEC-1). See "The
/// notice before a session ends" in docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// A session ends after a period of inactivity, and it ended without a word: a form that was
/// filled in for longer than the period was sent to nobody. So a page says shortly before that
/// its user is about to be signed out, and they can say that they are still there, which is
/// activity and starts the period anew. The session ends as before for a user who says nothing.
/// </para>
/// <para>
/// What a page is told here is what the session said when the page was written. Another window
/// of the same browser may have used the session since, so the page asks again before it says
/// anything, at <see cref="SessionAuthentication.SessionLeftPath"/>, and that question is no
/// activity.
/// </para>
/// </remarks>
public interface ISessionEnd
{
    /// <summary>
    /// When the session that a user is signed in with ends if nothing more is done with it, as
    /// the request of that user was told it.
    /// </summary>
    /// <param name="user">The user of a request.</param>
    /// <returns>Null for a user who is not signed in.</returns>
    SessionEnd? Of(ClaimsPrincipal user);
}

/// <summary>When a session ends if nothing more is done with it.</summary>
/// <param name="At">The moment that it ends.</param>
/// <param name="Left">How long that is from now. Never less than nothing.</param>
/// <param name="WarnsBefore">How long before it ends its user is told so: see <see cref="SessionAuthentication.WarnsBefore"/>.</param>
public readonly record struct SessionEnd(DateTimeOffset At, TimeSpan Left, TimeSpan WarnsBefore);

/// <summary>Answers from what a request was told when it was signed in. See <see cref="ISessionEnd"/>.</summary>
internal sealed class SessionEnds(SecuritySettings security, TimeProvider clock) : ISessionEnd
{
    private readonly TimeSpan _warnsBefore = SessionAuthentication.WarnsBefore(TimeSpan.FromMinutes(security.SessionIdleMinutes));

    public SessionEnd? Of(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (SignedInUser.SessionEndsOf(user) is not { } at)
        {
            return null;
        }

        var left = at - clock.GetUtcNow();

        return new SessionEnd(at, left > TimeSpan.Zero ? left : TimeSpan.Zero, _warnsBefore);
    }
}
