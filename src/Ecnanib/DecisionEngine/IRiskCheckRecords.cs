using Ecnanib.Platform.Persistence;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Reads the checks of the Risk Guard that were stored for an agent, so that its user sees
/// exactly why a plan was accepted or rejected (FR-RSK-9). See "The page" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// It takes the user by id, and not from a request: the Decision Engine may not use Accounts.
/// Whatever serves the request hands over the user who is signed in, and a check of anyone else
/// is not found (NFR-SEC-3). It only reads.
/// </remarks>
public interface IRiskCheckRecords
{
    /// <summary>The latest checks of an agent of a user, latest close first, each with the outcome of every rule.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<RiskCheckHistory> LatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>The checks of an agent that a page shows.</summary>
/// <param name="Total">How many checks are stored for the agent.</param>
/// <param name="Checks">The latest of them, latest close first, and the checks of one cycle in their order.</param>
public sealed record RiskCheckHistory(int Total, IReadOnlyList<RiskCheckView> Checks);

/// <summary>One check of the Risk Guard, as its user reads it.</summary>
/// <param name="CycleId">The cycle that the check was made in, whose timeline shows it beside the calls of the roles (FR-CYC-10).</param>
/// <param name="ClosedAt">The close of the candle that the cycle of the check was for.</param>
/// <param name="Mode">The mode that the agent was in then, which stays as it was when the agent is switched (FR-BIN-11).</param>
/// <param name="Sequence">Which check of its cycle: 1, or 2 for the close of a reverse that was rejected.</param>
/// <param name="Action">What the plan that was checked does.</param>
/// <param name="CheckedAt">When it was checked.</param>
/// <param name="Verdict">Whether the plan went on to the orders.</param>
/// <param name="Quantity">
/// The quantity of the order that the plan opens, which the check computed (FR-RSK-5). Zero when
/// the facts gave no order. Null for a plan that opens nothing, and when no size was computed.
/// </param>
/// <param name="Leverage">The leverage of that order: 1 on Spot. Null where the quantity is.</param>
/// <param name="Rules">The outcome of every rule that the check stored, in the order of <see cref="GuardRule"/>.</param>
/// <param name="Kind">
/// What started the cycle of the check. A check of a test cycle led to no order, whatever its
/// verdict, and its stale limit counted from the moment that the cycle was run (FR-CYC-12).
/// </param>
public sealed record RiskCheckView(
    Guid CycleId,
    DateTimeOffset ClosedAt,
    TradingMode Mode,
    int Sequence,
    PlanAction Action,
    DateTimeOffset CheckedAt,
    GuardVerdict Verdict,
    decimal? Quantity,
    int? Leverage,
    IReadOnlyList<RuleResult> Rules,
    CycleKind Kind = CycleKind.Scheduled);

/// <summary>Reads the stored checks of an agent, of the user that it is asked for.</summary>
internal sealed class RiskCheckRecords(IDbContextFactory<EcnanibDbContext> contexts) : IRiskCheckRecords
{
    /// <summary>How many checks a page shows.</summary>
    public const int Shown = 20;

    public async Task<RiskCheckHistory> LatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Both are asked for by owner: a check of another user, and a cycle of another user's
        // agent, are not found.
        var ofAgent =
            from check in context.Set<RiskCheck>().AsNoTracking().OwnedBy(userId)
            join cycle in context.Set<DecisionCycle>().AsNoTracking().OwnedBy(userId) on check.CycleId equals cycle.Id
            where cycle.AgentId == agentId
            select new { Check = check, cycle.ClosedAt, cycle.Mode, cycle.Kind, cycle.StartedAt };

        var total = await ofAgent.CountAsync(cancellationToken);
        var latest = await ofAgent
            .OrderByDescending(found => found.ClosedAt)

            // A test cycle is for a close that another cycle may be for as well: the checks of
            // one cycle stay together, the cycle that started last first, as the cycles are listed.
            .ThenByDescending(found => found.StartedAt)
            .ThenBy(found => found.Check.Sequence)
            .Take(Shown)
            .ToListAsync(cancellationToken);

        return new RiskCheckHistory(
            total,
            await RiskCheckViews.OfAsync(
                context, userId, [.. latest.Select(found => (found.Check, found.ClosedAt, found.Mode, found.Kind))], cancellationToken));
    }
}

/// <summary>
/// Makes the checks of the Risk Guard as a user reads them, each with the outcome of every rule:
/// for the page of the risk checks of an agent, and for the timeline of a cycle.
/// </summary>
internal static class RiskCheckViews
{
    /// <summary>The checks as a user reads them, in the order they are given in.</summary>
    /// <param name="context">The context that the checks were read with.</param>
    /// <param name="userId">The user whose checks they are: the rules are asked for by owner as well.</param>
    /// <param name="checks">The checks, each with the close, the mode, and the kind of its cycle.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<RiskCheckView>> OfAsync(
        EcnanibDbContext context,
        Guid userId,
        IReadOnlyList<(RiskCheck Check, DateTimeOffset ClosedAt, TradingMode Mode, CycleKind Kind)> checks,
        CancellationToken cancellationToken)
    {
        var ids = checks.Select(found => found.Check.Id).ToList();
        var rules = await context.Set<RiskCheckRule>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(rule => ids.Contains(rule.CheckId))
            .ToListAsync(cancellationToken);

        // A rule is stored by its name, so the database would sort them by the alphabet. They
        // are shown in the order of the rules.
        var byCheck = rules.ToLookup(rule => rule.CheckId);

        return
        [
            .. checks.Select(found => new RiskCheckView(
                found.Check.CycleId,
                found.ClosedAt,
                found.Mode,
                found.Check.Sequence,
                found.Check.Action,
                found.Check.CheckedAt,
                found.Check.Verdict,
                found.Check.Quantity,
                found.Check.Leverage,
                [
                    .. byCheck[found.Check.Id]
                        .OrderBy(rule => rule.Rule)
                        .Select(rule => new RuleResult(rule.Rule, rule.Outcome, rule.Measured, rule.Threshold)),
                ],
                found.Kind)),
        ];
    }
}
