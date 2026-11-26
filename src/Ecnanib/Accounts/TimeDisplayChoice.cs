using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// The time zone that the pages write a time in for the signed-in user, which is the user's own
/// choice: the one of their device, or UTC (NFR-USA-3). It is saved to the account, so it holds
/// on every device that the user signs in on. See docs/times.md.
/// </summary>
/// <remarks>
/// <para>
/// The user is the one of the session, and the choice of anyone else is not changed (NFR-SEC-3).
/// The choice exposes no key and moves no money, so it asks for no proof of identity, and is in
/// no event of the audit trail.
/// </para>
/// <para>
/// There is nothing to read here: a request is told the choice of its user as it is signed in,
/// with <see cref="SignedInUser.TimeDisplayOf"/>.
/// </para>
/// </remarks>
public interface ITimeDisplayChoice
{
    /// <summary>
    /// Saves what the signed-in user chose. It holds for every page that is asked for from then
    /// on, on every device. A choice that is made twice changes nothing the second time. It
    /// fails for a request that is not signed in, and for a choice that there is not.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="display">What the user chose.</param>
    Task SetAsync(HttpContext request, TimeDisplay display);
}

/// <summary>Stores the choice of a user in the row of their account.</summary>
internal sealed class StoredTimeDisplayChoice(IDbContextFactory<EcnanibDbContext> contexts, ILogger<StoredTimeDisplayChoice> logger)
    : ITimeDisplayChoice
{
    public async Task SetAsync(HttpContext request, TimeDisplay display)
    {
        // A number that names no choice would be stored as that number, and read by nothing.
        if (!Enum.IsDefined(display))
        {
            throw new ArgumentOutOfRangeException(nameof(display), display, "There is no such choice.");
        }

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The account of the session, and no other: no form names a user. Only where it says
        // something else, so that a choice that is made again writes nothing.
        var changed = await context.Set<User>()
            .Where(user => user.Id == userId && user.TimeDisplay != display)
            .ExecuteUpdateAsync(row => row.SetProperty(user => user.TimeDisplay, display), cancellationToken);

        if (changed > 0)
        {
            logger.LogInformation("User {UserId} chose the time zone {TimeDisplay}", userId, display);
        }
    }
}
