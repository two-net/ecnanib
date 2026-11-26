using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Keeps the global halt: reads it for an operator and for the Risk Guard, and switches it for
/// an operator (FR-OPS-3). See <see cref="IGlobalHaltSwitch"/>, and "The global halt" in
/// docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is here because of what it uses.</b> What keeps the halt answers the Risk Guard, and
/// records in the audit trail of Accounts. The Risk Guard and Accounts are leaves and know
/// nothing of each other, and this component may use both. See docs/architecture.md.
/// </para>
/// <para>
/// <b>Nothing is cached.</b> Whether the halt is on is read from its row each time it is asked,
/// so a switch holds from the next check on, on every instance of the application.
/// </para>
/// </remarks>
internal sealed class GlobalHaltSwitch(
    IDbContextFactory<EcnanibDbContext> contexts,
    TimeProvider clock,
    ILogger<GlobalHaltSwitch> logger) : IGlobalHaltSwitch, IGlobalHalt
{
    public async Task<bool> IsOnAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // A flag that was never switched has no row, and is off.
        return await context.Set<RuntimeFlag>()
            .AsNoTracking()
            .Where(flag => flag.Name == RuntimeFlag.GlobalHalt)
            .Select(flag => flag.Value)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<GlobalHaltState> StateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var flag = await context.Set<RuntimeFlag>()
            .AsNoTracking()
            .SingleOrDefaultAsync(flag => flag.Name == RuntimeFlag.GlobalHalt, cancellationToken);

        return flag is { Value: true } ? new GlobalHaltState(On: true, flag.ChangedAt) : new GlobalHaltState(On: false, Since: null);
    }

    public async Task<bool> SwitchAsync(HttpContext request, bool on)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Asked here as well as by the page: whatever calls this, only an operator switches the
        // halt (NFR-SEC-3). It is false for a request that is not signed in.
        if (!OperatorAccess.IsOperator(request.User))
        {
            logger.LogWarning(
                "User {UserId} was refused switching the global halt, which is for an operator", SignedInUser.IdOf(request));

            return false;
        }

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The account first, as everything that records an action of a user does: the event
        // points at it, and a deletion of the account comes before this or waits for it.
        if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted, on another browser, since this request was signed in.
            return false;
        }

        var now = clock.GetUtcNow();

        // Written only where it changes the flag, so the answer says whether this request
        // switched it. Of two requests at once the second waits for the row, and finds the
        // flag as the first one left it.
        int switched;

        if (on)
        {
            switched = await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO runtime_flags (name, value, changed_by, changed_at)
                VALUES ({RuntimeFlag.GlobalHalt}, TRUE, {userId}, {now})
                ON CONFLICT (name) DO UPDATE
                SET value = TRUE, changed_by = EXCLUDED.changed_by, changed_at = EXCLUDED.changed_at
                WHERE NOT runtime_flags.value
                """,
                cancellationToken);
        }
        else
        {
            // A flag without a row is off already, and gets none for being switched off.
            switched = await context.Set<RuntimeFlag>()
                .Where(flag => flag.Name == RuntimeFlag.GlobalHalt && flag.Value)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(flag => flag.Value, false)
                        .SetProperty(flag => flag.ChangedBy, (Guid?)userId)
                        .SetProperty(flag => flag.ChangedAt, now),
                    cancellationToken);
        }

        if (switched == 0)
        {
            // It was as asked for: the form was sent twice, or another operator was first.
            // Nothing was done, so nothing is recorded.
            return false;
        }

        await AuditTrail.RecordAsync(
            context,
            userId,
            on ? AuditAction.GlobalHaltSwitchedOn : AuditAction.GlobalHaltSwitchedOff,
            AuditTarget.GlobalHalt,
            now,
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        if (on)
        {
            logger.LogInformation("User {UserId} switched the global halt on: no agent opens a new position until it is off", userId);
        }
        else
        {
            logger.LogInformation("User {UserId} switched the global halt off: agents open positions again", userId);
        }

        return true;
    }
}
