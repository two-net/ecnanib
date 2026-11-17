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
public sealed record RiskCheckView(
    DateTimeOffset ClosedAt,
    TradingMode Mode,
    int Sequence,
    PlanAction Action,
    DateTimeOffset CheckedAt,
    GuardVerdict Verdict,
    decimal? Quantity,
    int? Leverage,
    IReadOnlyList<RuleResult> Rules);

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
            select new { Check = check, cycle.ClosedAt, cycle.Mode };

        var total = await ofAgent.CountAsync(cancellationToken);
        var latest = await ofAgent
            .OrderByDescending(found => found.ClosedAt)
            .ThenBy(found => found.Check.Sequence)
            .Take(Shown)
            .ToListAsync(cancellationToken);

        var ids = latest.Select(found => found.Check.Id).ToList();
        var rules = await context.Set<RiskCheckRule>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(rule => ids.Contains(rule.CheckId))
            .ToListAsync(cancellationToken);

        // A rule is stored by its name, so the database would sort them by the alphabet. They
        // are shown in the order of the rules.
        var byCheck = rules.ToLookup(rule => rule.CheckId);

        return new RiskCheckHistory(
            total,
            [
                .. latest.Select(found => new RiskCheckView(
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
                    ])),
            ]);
    }
}
