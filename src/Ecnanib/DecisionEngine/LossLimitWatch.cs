using System.Collections.Concurrent;
using System.Globalization;
using Ecnanib.Agents;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Watches the loss limits of an agent (FR-RSK-8): when its loss for the UTC day reaches its
/// daily loss limit, it closes its position and halts it until the next UTC day, and when its
/// drawdown from its peak equity reaches its drawdown limit, it closes its position and halts it
/// until its user resumes it. Both include unrealized profit and loss. It is asked at every cycle
/// and every reconciliation. See <see cref="ILossLimits"/> and "Halting an agent" in
/// docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule is the Risk Guard's.</b> <see cref="LossLimits"/> says when a limit is reached, of
/// the equity that <see cref="AgentResults"/> works out from the rows, and of the marks that are
/// kept here: the result of the agent as the UTC day began, and the highest that it has been.
/// The rule that rejects an entry asks the same of the same numbers (FR-RSK-4), so an agent that
/// is not halted yet opens nothing either.
/// </para>
/// <para>
/// <b>One evaluation is one transaction, under the lock of the account,</b> which the controls
/// of the agent's user take: the state of the agent is read, the marks are written, and the
/// agent is halted, all or none. So a halt and a pause of its user cannot cross, and two
/// evaluations of one agent happen one after the other. Nothing of the exchange is asked inside
/// it: the result and the price are read before it, and the position is closed after it.
/// </para>
/// <para>
/// <b>Only an agent that runs is halted.</b> One that its user paused or stopped, or that waits
/// for its user, keeps its position with its protective orders (SRS 3.3). Its marks are kept all
/// the same, so that its loss of the day is counted from the day's start when it runs again.
/// </para>
/// <para>
/// <b>The agent is halted first, and its position is closed after,</b> as a stop of its user
/// does it. So a position that could not be closed is that of a halted agent, which opens
/// nothing more, with its stop-loss: it is closed again with a later evaluation, until the
/// agent holds nothing. A close that failed is not asked for again at once: each one takes the
/// protection of the position off and places it again, which the account event feed says as a
/// change, and a change is a reconciliation, which evaluates.
/// </para>
/// <para>
/// <b>Once an agent is halted, what follows is not given up,</b> also when what asked is being
/// stopped: its user is told, and its position is closed. What is left undone then is in the
/// rows, and the next evaluation closes again.
/// </para>
/// <para>
/// <b>At a drawdown halt the peak starts anew,</b> at the result that reached the limit. Its
/// user resumes such an agent, and its drawdown is counted from there: it would be halted again
/// at its first cycle otherwise.
/// </para>
/// <para>
/// <b>An equity that is not known halts nothing.</b> Where the exchange did not say the price
/// that the open position is valued at, no mark is written and no agent is halted: the next
/// evaluation asks again, and the stop-loss of the position rests on the exchange meanwhile.
/// </para>
/// <para>
/// <b>An evaluation records the equity that it found,</b> in its transaction, when that is due:
/// the rows are the equity of the agent over time. See <see cref="EquitySnapshots.IsDue"/>. An
/// equity that is not known is not recorded.
/// </para>
/// <para>
/// It places no order itself, and is handed no key: the close is asked of what places the
/// orders, as the close of a user is.
/// </para>
/// </remarks>
internal sealed class LossLimitWatch(
    IDbContextFactory<EcnanibDbContext> contexts,
    AgentResults results,
    IAgentTrading trading,
    IAgentHalts halts,
    IPlanOrders orders,
    StoredOrders stored,
    INotifications notifications,
    TimeProvider clock,
    ILogger<LossLimitWatch> logger) : ILossLimits
{
    /// <summary>
    /// The time in which the user of a halted agent is told once that its position could not be
    /// closed. It is tried again with every evaluation, which comes every few minutes.
    /// </summary>
    public static readonly TimeSpan CloseFailedSaidEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// How long after a close that failed the position of a halted agent is not closed again.
    /// It is shorter than the time between two reconciliations, so each of those closes again,
    /// and long enough that the changes which a close that fails makes in the account, which
    /// the account event feed says at once, start no close of their own.
    /// </summary>
    public static readonly TimeSpan CloseAgainAfter = TimeSpan.FromMinutes(2);

    // When the close of the position of a halted agent last failed, in this process: an
    // application that starts closes again at once.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _closeFailedAt = new();

    public async Task<LossLimitVerdict> EvaluateAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Read with no lock held: the sums of the rows, and the price of the open position.
        var result = await results.OfAsync(agent, cancellationToken);

        var (state, reached) = await DecideAsync(agent, result, cancellationToken);

        if (state is null)
        {
            // The account or the agent is gone: there is nothing to watch.
            return LossLimitVerdict.Within;
        }

        if (reached is { } limit)
        {
            logger.LogWarning(
                "Agent {AgentId} reached its {LossLimit} limit, with a loss of {LossPercent} percent: it is halted, and its position is closed at market",
                agent.Id,
                limit.Limit,
                limit.LossPercent);

            await TellAsync(agent, limit);
        }

        var halted = reached is not null || state == AgentState.Halted;

        // The position of a halted agent is closed: now, and again with a later evaluation
        // while it is open still, or while an entry of the agent may still open one. The agent
        // is halted by now, so this is not given up when what asked is being stopped.
        if (halted && await HoldsAsync(agent.Id, CancellationToken.None))
        {
            if (reached is null
                && _closeFailedAt.TryGetValue(agent.Id, out var failedAt)
                && clock.GetUtcNow() - failedAt < CloseAgainAfter)
            {
                logger.LogDebug(
                    "The position of agent {AgentId}, which is halted, is not closed again yet: its close failed a moment ago", agent.Id);
            }
            else
            {
                await CloseAsync(agent);
            }
        }

        return halted ? LossLimitVerdict.Halted : result.Valued ? LossLimitVerdict.Within : LossLimitVerdict.NotKnown;
    }

    /// <summary>
    /// Under the lock of the account: reads the state of the agent, keeps its marks, and halts
    /// it when it runs and has reached a limit.
    /// </summary>
    /// <returns>The state that the agent was in, or null when it is gone, and the limit that it was halted for now, or null.</returns>
    private async Task<(AgentState? State, Reached? Reached)> DecideAsync(
        Agent agent, AgentResult result, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await trading.StateUnderLockAsync(context, agent.UserId, agent.Id, cancellationToken) is not { } state)
        {
            return (null, null);
        }

        // One moment for the whole evaluation, taken under the lock: the UTC day that the loss
        // is counted in, and since when the agent is halted. So a halt is later than a change
        // of the state that held the lock before it, and an agent that was resumed as a new
        // day began is evaluated in that day, and not for the loss of the day before.
        var now = clock.GetUtcNow();

        if (!result.Valued)
        {
            logger.LogInformation(
                "The loss limits of agent {AgentId} are not evaluated: its equity is not known. They are evaluated again the next time",
                agent.Id);

            return (state, null);
        }

        var before = await context.Set<EquityMark>()
            .AsNoTracking()
            .SingleOrDefaultAsync(mark => mark.AgentId == agent.Id && mark.Mode == agent.Mode, cancellationToken);

        var (dayBeganAt, atDayStart, peak) = EquityMarks.At(before, result.Net, agent.AllocatedCapital, agent.QuoteAsset, now);

        // The capital as it is now is under the result and under both marks, which were worked
        // out for it: a capital that its user changed reaches no limit by itself.
        var equity = agent.AllocatedCapital + result.Net;
        var equityAtDayStart = agent.AllocatedCapital + atDayStart;
        var peakEquity = agent.AllocatedCapital + peak;

        Reached? reached = null;

        try
        {
            reached = LossLimits.Reached(equity, equityAtDayStart, peakEquity, agent.DailyLossPercent, agent.DrawdownPercent) switch
            {
                LossLimit.Drawdown => new Reached(
                    LossLimit.Drawdown, equity, peakEquity, Percent(LossLimits.LossPercent(equity, peakEquity)), agent.DrawdownPercent),
                LossLimit.DailyLoss => new Reached(
                    LossLimit.DailyLoss, equity, equityAtDayStart, Percent(LossLimits.LossPercent(equity, equityAtDayStart)), agent.DailyLossPercent),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            // Numbers that cannot be worked out with are no numbers of a market: nothing is halted on them.
            logger.LogWarning("The loss limits of agent {AgentId} are not evaluated: its equity is beyond what can be worked out with", agent.Id);
        }

        // Only an agent that runs is halted. Agents says so again, under this same lock.
        if (reached is { } limit
            && !(AgentStates.RunsCycles(state)
                && await halts.HaltAsync(context, agent.UserId, agent.Id, CauseOf(limit.Limit), now, cancellationToken)))
        {
            logger.LogDebug(
                "Agent {AgentId} has reached its {LossLimit} limit and does not run, so it stays {AgentState}", agent.Id, limit.Limit, state);

            reached = null;
        }

        // The peak of an agent that its drawdown limit halts starts anew at the result that
        // reached the limit: its drawdown is counted from there once its user resumes it.
        if (reached is { Limit: LossLimit.Drawdown })
        {
            peak = result.Net;
        }

        await KeepAsync(context, agent, before, dayBeganAt, atDayStart, peak, now, cancellationToken);
        await RecordAsync(context, agent, equity, holds: result.Open is not null, now, cancellationToken);

        // Committed whatever becomes of what asked: a commit that is given up as it lands
        // would leave an agent halted, and nobody told.
        await transaction.CommitAsync(CancellationToken.None);

        return (state, reached);
    }

    /// <summary>Writes the marks of an agent, when they are not as they are stored.</summary>
    private static async Task KeepAsync(
        EcnanibDbContext context,
        Agent agent,
        EquityMark? before,
        DateTimeOffset dayBeganAt,
        decimal atDayStart,
        decimal peak,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (before is null)
        {
            context.Add(new EquityMark(
                agent.Id, agent.UserId, agent.Mode, agent.QuoteAsset, agent.AllocatedCapital, dayBeganAt, atDayStart, peak, now));
            await context.SaveChangesAsync(cancellationToken);

            return;
        }

        if (before.Asset == agent.QuoteAsset
            && before.Capital == agent.AllocatedCapital
            && before.DayBeganAt == dayBeganAt
            && before.ResultAtDayStart == atDayStart
            && before.PeakResult == peak)
        {
            return;
        }

        await context.Set<EquityMark>()
            .Where(mark => mark.AgentId == agent.Id && mark.Mode == agent.Mode)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(mark => mark.Asset, agent.QuoteAsset)
                    .SetProperty(mark => mark.Capital, agent.AllocatedCapital)
                    .SetProperty(mark => mark.DayBeganAt, dayBeganAt)
                    .SetProperty(mark => mark.ResultAtDayStart, atDayStart)
                    .SetProperty(mark => mark.PeakResult, peak)
                    .SetProperty(mark => mark.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// Records the equity of an agent as it was evaluated, when that is due: the equity of an
    /// agent over time is these rows. See <see cref="EquitySnapshots.IsDue"/>.
    /// </summary>
    private static async Task RecordAsync(
        EcnanibDbContext context, Agent agent, decimal equity, bool holds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var newest = await context.Set<EquitySnapshot>()
            .AsNoTracking()
            .Where(snapshot => snapshot.AgentId == agent.Id && snapshot.Mode == agent.Mode)
            .OrderByDescending(snapshot => snapshot.At)
            .FirstOrDefaultAsync(cancellationToken);

        if (!EquitySnapshots.IsDue(newest, agent.QuoteAsset, agent.AllocatedCapital, equity, holds, now))
        {
            return;
        }

        context.Add(new EquitySnapshot(agent.Id, agent.UserId, agent.Mode, now, agent.QuoteAsset, agent.AllocatedCapital, equity));
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Whether an agent holds a position, or has an entry that is not over and may open one.</summary>
    private async Task<bool> HoldsAsync(Guid agentId, CancellationToken cancellationToken) =>
        await stored.OpenPositionAsync(agentId, cancellationToken) is not null
        || (await stored.LiveOrdersAsync(agentId, cancellationToken)).Any(order => order.Purpose == OrderPurpose.Entry);

    /// <summary>
    /// Has the position of a halted agent closed at market. The agent is halted by now, so the
    /// close is not given up when what asked is stopped: what it leaves undone is in the rows,
    /// and the next evaluation closes again.
    /// </summary>
    private async Task CloseAsync(Agent agent)
    {
        var outcome = await orders.CloseForLossLimitAsync(agent.Id, CancellationToken.None);

        if (outcome != PositionCloseOutcome.Failed)
        {
            _closeFailedAt.TryRemove(agent.Id, out _);

            logger.LogInformation("The position of agent {AgentId}, which is halted, was closed at market: {PositionCloseOutcome}", agent.Id, outcome);

            return;
        }

        _closeFailedAt[agent.Id] = clock.GetUtcNow();

        logger.LogWarning(
            "The position of agent {AgentId}, which is halted, could not be closed at market. It is open as it was, and is closed again with a later evaluation",
            agent.Id);

        try
        {
            await notifications.RaiseOnceAsync(
                agent.UserId,
                NotificationType.ProtectionFailure,
                agent.Id,
                agent.Mode,
                $"The position of the halted agent in {agent.Symbol} ({agent.Mode}) could not be closed at market. It is open as it was, " +
                "and the service tries again within minutes. Close it with \"Close position now\", or at Binance. " +
                "This is said once in an hour for an agent.",
                CloseFailedSaidEvery,
                CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(
                error, "The user {UserId} could not be told that the position of the halted agent {AgentId} could not be closed", agent.UserId, agent.Id);
        }
    }

    /// <summary>
    /// Tells the user of an agent that it was halted, with the figures in plain words
    /// (FR-NTF-1). A notification that cannot be stored stops nothing: the position is closed
    /// all the same, and the page of the agent says its state, the cause, and the time.
    /// </summary>
    private async Task TellAsync(Agent agent, Reached reached)
    {
        var asset = agent.QuoteAsset;

        var content = reached.Limit == LossLimit.Drawdown
            ? $"The agent reached its drawdown limit in {agent.Symbol} ({agent.Mode}): its equity is {Amount(reached.Equity)} {asset}, " +
              $"{Written(reached.LossPercent)}% below its peak of {Amount(reached.From)} {asset}, and its limit is {Written(reached.LimitPercent)}%. " +
              "It is halted, and its position is closed at market. It stays halted until you resume it."
            : $"The agent reached its daily loss limit in {agent.Symbol} ({agent.Mode}): its equity is {Amount(reached.Equity)} {asset}, " +
              $"{Written(reached.LossPercent)}% below the {Amount(reached.From)} {asset} that it had as the UTC day began, and its limit is " +
              $"{Written(reached.LimitPercent)}%. It is halted, and its position is closed at market. The service resumes it when the next " +
              "UTC day begins.";

        try
        {
            await notifications.RaiseAsync(agent.UserId, NotificationType.AgentHalted, agent.Id, agent.Mode, content, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The user {UserId} could not be told that agent {AgentId} was halted", agent.UserId, agent.Id);
        }
    }

    private static AgentStateCause CauseOf(LossLimit limit) =>
        limit == LossLimit.Drawdown ? AgentStateCause.DrawdownLimitReached : AgentStateCause.DailyLossLimitReached;

    // A loss in percent, as it is said: to 2 decimal places.
    private static decimal Percent(decimal percent) => Math.Round(percent, 2, MidpointRounding.AwayFromZero);

    // An amount of the asset that the symbol is priced in, as it is said: to 2 decimal places.
    private static string Amount(decimal amount) => Written(Math.Round(amount, 2, MidpointRounding.AwayFromZero));

    private static string Written(decimal number) => number.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A loss limit that an agent reached, with the numbers that say so.</summary>
    /// <param name="Limit">Which limit.</param>
    /// <param name="Equity">The equity of the agent, with unrealized profit and loss.</param>
    /// <param name="From">What the loss is counted from: the equity as the UTC day began, or the peak equity.</param>
    /// <param name="LossPercent">The loss, in percent of that.</param>
    /// <param name="LimitPercent">The limit of the agent, in percent.</param>
    private sealed record Reached(LossLimit Limit, decimal Equity, decimal From, decimal LossPercent, decimal LimitPercent);
}
