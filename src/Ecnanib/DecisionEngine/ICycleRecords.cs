using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Records a cycle, the calls of its roles (FR-TEAM-8), and the checks of the Risk Guard
/// (FR-RSK-9): with the summary of its data package and its outcome, which the cycle carries,
/// and with its orders, which are rows of their own under its id, a complete record (FR-CYC-10).
/// See "The record of a cycle" in docs/team.md and "The record of a check" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// It is public so that a test can put a stand-in behind it, for cycles of agents that need no
/// rows: the tests see no internal type.
/// </remarks>
public interface ICycleRecords
{
    /// <summary>
    /// Stores a cycle with the calls of its roles and the checks of the Risk Guard, all of them
    /// or none: a cycle is recorded once, when it ends.
    /// </summary>
    /// <param name="cycle">The cycle, with how it ended, the summary of its data package, and why it placed no entry where it placed none.</param>
    /// <param name="outputs">The calls of its roles, in the order they were made. Empty when none was made.</param>
    /// <param name="checks">
    /// What the Risk Guard made of what went on of the plan, in the order of the checks, each
    /// with the outcome of every rule. Empty when nothing went on.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task StoreAsync(
        DecisionCycle cycle,
        IReadOnlyList<RoleOutput> outputs,
        IReadOnlyList<RiskCheckRecord> checks,
        CancellationToken cancellationToken);
}

/// <summary>Stores a cycle, the calls of its roles, and the checks of the Risk Guard in one transaction.</summary>
internal sealed class CycleRecords(IDbContextFactory<EcnanibDbContext> contexts) : ICycleRecords
{
    public async Task StoreAsync(
        DecisionCycle cycle,
        IReadOnlyList<RoleOutput> outputs,
        IReadOnlyList<RiskCheckRecord> checks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(checks);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        context.Add(cycle);
        context.AddRange(outputs);

        foreach (var check in checks)
        {
            context.Add(check.Check);
            context.AddRange(check.Rules);
        }

        // What Experience is to judge of the cycle once the candles after it have closed: the
        // calls of its analysts (FR-EXP-3), and its plan where that was vetoed (FR-EXP-5). They
        // are rows of Experience, which say what the record says, and are written with it.
        context.AddRange(CycleExperience.Of(cycle, outputs));

        // One save is one transaction: the cycle, its calls, its checks, and what Experience
        // is to judge of it are stored together.
        await context.SaveChangesAsync(cancellationToken);
    }
}
