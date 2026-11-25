using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// What the signed-in user said on the setup checklist of a first-time user (FR-UI-4): that they
/// reviewed decisions, and that they considered Live, each with when. They are stored with the
/// account, so the checklist is the same on every device that the user signs in on. See
/// docs/dashboard.md.
/// </summary>
/// <remarks>
/// The user is the one of the session, and the marks of anyone else are neither read nor set
/// (NFR-SEC-3). A mark exposes no key and moves no money, so it asks for no proof of identity,
/// and is in no event of the audit trail.
/// </remarks>
public interface ISetupMarks
{
    /// <summary>
    /// What the signed-in user said so far. It fails for a request that is not signed in. An
    /// account that was deleted since the request was signed in said nothing.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    Task<SetupMarks> ReadAsync(HttpContext request);

    /// <summary>
    /// Stores that the signed-in user says so, as of now. The first time stands: a mark that is
    /// set again changes nothing, and none is taken back. It fails for a request that is not
    /// signed in, and for a mark that there is not.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="mark">What the user says.</param>
    Task SetAsync(HttpContext request, SetupMark mark);
}

/// <summary>What a user said on the setup checklist, each with when, or null while they have not.</summary>
/// <param name="DecisionsReviewedAt">When they said that they reviewed what a team of theirs decided.</param>
/// <param name="LiveConsideredAt">When they said that they considered Live, or first switched an agent to Live.</param>
public sealed record SetupMarks(DateTimeOffset? DecisionsReviewedAt, DateTimeOffset? LiveConsideredAt)
{
    /// <summary>
    /// Whether the user said both. Whoever reviewed decisions and considered Live is no
    /// first-time user: the checklist is not shown to them again, whatever they delete later.
    /// </summary>
    public bool Both => DecisionsReviewedAt is not null && LiveConsideredAt is not null;
}

/// <summary>
/// Writes a mark of the setup checklist: the one way one is stored, for Accounts and for a
/// component that does what a mark says, as <see cref="AuditTrail"/> is for an event.
/// </summary>
public static class SetupMarking
{
    /// <summary>
    /// Stores a mark of a user with the context of whoever does what the mark says. A caller
    /// that holds a transaction, such as a switch of an agent to Live, has the mark stored when
    /// the action is, and not when it is not. The first time stands.
    /// </summary>
    /// <param name="context">The context to write with, and with it the transaction that it holds, if any.</param>
    /// <param name="userId">The user whom the mark is of.</param>
    /// <param name="mark">What the user said, or did.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the mark was stored now: false when it stood already, or the account is gone.</returns>
    public static async Task<bool> RecordAsync(
        EcnanibDbContext context, Guid userId, SetupMark mark, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The account that the caller names, and only where the mark is empty: what was said
        // first is what stands.
        var users = context.Set<User>().Where(user => user.Id == userId);

        var changed = mark switch
        {
            SetupMark.DecisionsReviewed => await users
                .Where(user => user.DecisionsReviewedAt == null)
                .ExecuteUpdateAsync(row => row.SetProperty(user => user.DecisionsReviewedAt, at), cancellationToken),

            SetupMark.LiveConsidered => await users
                .Where(user => user.LiveConsideredAt == null)
                .ExecuteUpdateAsync(row => row.SetProperty(user => user.LiveConsideredAt, at), cancellationToken),

            // A number that names no mark would be stored as nothing, and read by nothing.
            _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, "There is no such mark."),
        };

        return changed > 0;
    }
}

/// <summary>Stores what a user said on the setup checklist in the row of their account.</summary>
internal sealed class StoredSetupMarks(
    IDbContextFactory<EcnanibDbContext> contexts, TimeProvider clock, ILogger<StoredSetupMarks> logger) : ISetupMarks
{
    public async Task<SetupMarks> ReadAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The account of the session, and no other: no path and no form names a user.
        var marks = await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => new SetupMarks(user.DecisionsReviewedAt, user.LiveConsideredAt))
            .SingleOrDefaultAsync(cancellationToken);

        // The account was deleted, on another browser, since this request was signed in.
        return marks ?? new SetupMarks(null, null);
    }

    public async Task SetAsync(HttpContext request, SetupMark mark)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        if (await SetupMarking.RecordAsync(context, userId, mark, clock.GetUtcNow(), cancellationToken))
        {
            logger.LogInformation("User {UserId} marked {SetupMark} on the setup checklist", userId, mark);
        }
    }
}
