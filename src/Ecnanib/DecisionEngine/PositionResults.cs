using System.Globalization;
using Ecnanib.Agents;
using Ecnanib.Experience;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Records the result of every closed position (FR-EXP-1): its net profit or loss, its result in
/// R, its fees, its funding, and how long it was held, from the orders, the executions, and the
/// funding that the Decision Engine keeps of it. Fees and funding are what the exchange said
/// (FR-BIN-10). See <see cref="PositionResult"/> and "The results of a position" in
/// docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A position has its result once all of it is stored:</b> it is closed, no order of it is
/// with the exchange still, the executions of its orders are stored, each with what its fee is
/// worth, and its funding is read. What settles the orders stores the executions within a
/// minute of a close, and what watches the positions reads the funding of a position of the
/// Futures market some minutes after it. So a position of the Spot market has its result within
/// minutes, and one of the Futures market some minutes later.
/// </para>
/// <para>
/// <b>What could not be read is not waited for longer than it is asked for.</b> Executions and
/// funding are asked for until a day after a close. A position that still lacks something
/// <see cref="AtTheLatest"/> after it was closed is given its result with what there is, and
/// says that it is not complete.
/// </para>
/// <para>
/// <b>The sums are those that the equity of its agent is worked out with:</b>
/// <see cref="ResultSums"/>. So the results of the positions of an agent add up to what the
/// agent made, less the fees of the entries that opened nothing.
/// </para>
/// <para>
/// <b>A result is written once.</b> The rows that it is worked out from no longer change by
/// then, so they are read in a snapshot and the row is written after it. Of two that record the
/// same position, the key of the table lets one write.
/// </para>
/// <para>
/// <b>Recording a result evaluates the loss limits of its agent,</b> which records its equity
/// (FR-RSK-8): the last fee and the last funding of a position are stored after it is closed,
/// and an agent that no longer runs and holds nothing is evaluated by nothing else. See
/// <see cref="EquitySnapshot"/>.
/// </para>
/// <para>
/// <b>Recording a result leaves the lesson of the position to be written</b> (FR-EXP-6): a row
/// of Experience, with what the position says and nothing of a lesson yet, in the same
/// transaction as the result. So a position has its lesson to be written exactly when it has its
/// result, and a lesson that its user deleted is not written again. The model of the agent's Risk
/// Manager is called for it by a job of its own: see <see cref="LessonWriting"/>.
/// </para>
/// <para>
/// It only reads the rows, and writes the results: it asks no exchange, and is handed no key.
/// </para>
/// </remarks>
internal sealed class PositionResults(
    IDbContextFactory<EcnanibDbContext> contexts,
    IAgentHalts agents,
    ILossLimits limits,
    INotifications notifications,
    TimeProvider clock,
    ILogger<PositionResults> logger)
{
    /// <summary>
    /// How long after it was closed a position is given its result at the latest, with what is
    /// stored of it by then: the time that its executions and its funding are asked for, and an
    /// hour, in which the last read that began in that time is over.
    /// </summary>
    public static readonly TimeSpan AtTheLatest = StoredOrders.FillsWithin + TimeSpan.FromHours(1);

    /// <summary>How many positions a run records at most. The rest are recorded with the next runs.</summary>
    public const int Most = 200;

    /// <summary>
    /// The closed positions, of all users, whose result is due and not recorded yet, in the
    /// order of their ids: at most <see cref="Most"/> of them.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var noLonger = now - AtTheLatest;
        var valuedSince = now - StoredOrders.FeesValuedWithin;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var orders = context.Set<Order>().AsNoTracking();
        var fills = context.Set<Fill>().AsNoTracking();
        var results = context.Set<PositionResult>().AsNoTracking();

        return await context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.ClosedAt != null
                && !results.Any(result => result.PositionId == position.Id)
                && (position.ClosedAt < noLonger

                    // All of it is stored: its funding is read, none of its orders is with the
                    // exchange still, their executions are stored, and each fee has its value,
                    // or is no longer valued.
                    || (position.FundingDueAt == null
                        && !orders.Any(order => order.PositionId == position.Id
                            && (OrderStatuses.Live.Contains(order.Status) || (order.ExecutedQuantity > 0m && !order.FillsRecorded)))
                        && !orders.Any(order => order.PositionId == position.Id
                            && order.UpdatedAt >= valuedSince
                            && fills.Any(fill => fill.OrderId == order.Id && fill.FeeValue == null)))))
            .OrderBy(position => position.Id)
            .Select(position => position.Id)
            .Take(Most)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Records the results that are due, each on its own, and then has the loss limits of the
    /// agents of those positions evaluated, which records their equity.
    /// </summary>
    /// <returns>How many results were recorded.</returns>
    public async Task<int> RecordDueAsync(CancellationToken cancellationToken)
    {
        var recorded = 0;
        var of = new HashSet<Guid>();

        foreach (var positionId in await DueAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await RecordAsync(positionId, cancellationToken) is { } agentId)
                {
                    recorded++;
                    of.Add(agentId);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Said once, with the position. The others go on, and the next run tries again.
                logger.LogError(error, "The result of the position {PositionId} could not be recorded", positionId);
            }
        }

        foreach (var agentId in of)
        {
            await EvaluateAsync(agentId, cancellationToken);
        }

        return recorded;
    }

    /// <summary>
    /// Records the result of one closed position, from its rows as they are stored now.
    /// </summary>
    /// <returns>The agent of the position when its result was written, or null when nothing was: the position is not closed, or has its result already.</returns>
    public async Task<Guid?> RecordAsync(Guid positionId, CancellationToken cancellationToken)
    {
        if (await OfAsync(positionId, cancellationToken) is not var (position, result))
        {
            return null;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        context.Add(result);

        // The lesson of the position, to be written: with the result, or not at all. A position
        // has one of each, so another that recorded the result meanwhile left the lesson too.
        context.Add(Lesson.Due(
            position.Id,
            position.UserId,
            position.AgentId,
            position.Mode,
            position.Market,
            position.Symbol,
            position.Timeframe,
            position.ClosedAt ?? result.RecordedAt,
            result.ResultInR,
            result.RecordedAt));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another has recorded it meanwhile: a position has one result. Anything else that
            // the database refused is an error, and is said as one.
            if (!await IsRecordedAsync(positionId))
            {
                throw;
            }

            return null;
        }

        logger.LogInformation(
            "The result of the position of agent {AgentId} in {Symbol}, which was closed as {ExitReason}, was recorded: complete {Complete}",
            position.AgentId,
            position.Symbol,
            position.ExitReason,
            result.Complete);

        // Its user is told that it was closed now, and once: with what it made, which is known
        // when its result is, and a position has one result (FR-NTF-1).
        await TellAsync(position, result);

        return position.AgentId;
    }

    /// <summary>
    /// Tells the user of a position that it was closed, how, and what it made. That it could
    /// not be told is said, and stops nothing: the result is recorded.
    /// </summary>
    private async Task TellAsync(Position position, PositionResult result)
    {
        try
        {
            await notifications.RaiseAsync(
                position.UserId, NotificationType.PositionClosed, position.AgentId, ClosedSays(position, result), CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(
                error, "The user {UserId} could not be told that the position of agent {AgentId} was closed", position.UserId, position.AgentId);
        }
    }

    /// <summary>
    /// What the notification of a position that was closed says: how it was closed, and its net
    /// result in the asset that its symbol is priced in, and in R where that is known. The side
    /// is said on the Futures market, where a position is long or short, and so is the funding.
    /// </summary>
    internal static string ClosedSays(Position position, PositionResult result)
    {
        var spot = position.Market == Market.Spot;
        var what = spot ? "The position" : position.Side == PositionSide.Long ? "The long position" : "The short position";

        var how = position.ExitReason switch
        {
            ExitReason.StopLoss => " by its stop-loss",
            ExitReason.TakeProfit => " by its take-profit",
            ExitReason.TeamClose => " by the team of the agent",
            ExitReason.UserClose => " by you",
            ExitReason.ProtectionFailed => " by the service, because its protection could not be placed",
            ExitReason.LossLimit => " by the service, because the agent reached a loss limit",
            ExitReason.ClosedAtExchange => " at Binance, and by no order of the agent",
            _ => string.Empty,
        };

        var made = result.NetResult < 0m ? "lost" : "made";
        var after = spot ? "after fees" : "after fees and funding";

        // To 2 decimal places, as a multiple of what was risked is read: -1 R is the whole of it.
        var inR = result.ResultInR is { } r
            ? $", which is {Math.Round(r, 2, MidpointRounding.AwayFromZero).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)} R"
            : string.Empty;

        var complete = result.Complete ? string.Empty : " Not all of it could be read from Binance, so this is what is known of it.";

        return $"{what} in {position.Symbol} ({position.Mode}) was closed{how}. It {made} {Written.Number(Math.Abs(result.NetResult))} " +
            $"{result.Asset} {after}{inR}.{complete}";
    }

    /// <summary>
    /// The result of a closed position, worked out from its rows in one snapshot, or null when
    /// there is none to write: the position is not closed, or has its result.
    /// </summary>
    private async Task<(Position Position, PositionResult Result)?> OfAsync(Guid positionId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        var positions = context.Set<Position>().AsNoTracking().Where(row => row.Id == positionId);

        if (await positions.SingleOrDefaultAsync(cancellationToken) is not { ClosedAt: { } closedAt } position
            || await context.Set<PositionResult>().AsNoTracking().AnyAsync(row => row.PositionId == positionId, cancellationToken))
        {
            return null;
        }

        var orders = context.Set<Order>().AsNoTracking().Where(order => order.PositionId == positionId);

        var (made, priced) = await ResultSums.MadeAsync(context, positions, cancellationToken);
        var (fees, valued) = await ResultSums.FeesAsync(context, orders, cancellationToken);
        var funding = await ResultSums.FundingAsync(context, positions, cancellationToken);

        var rows = await orders
            .OrderBy(order => order.Id)
            .Select(order => new
            {
                order.Id,
                order.Purpose,
                order.Status,
                order.ExecutedQuantity,
                order.AveragePrice,
                order.StopLoss,
                order.FillsRecorded,
            })
            .ToListAsync(cancellationToken);

        var entries = rows.Where(order => order.Purpose == OrderPurpose.Entry).Select(order => order.Id).ToList();

        var executions = await context.Set<Fill>()
            .AsNoTracking()
            .Where(fill => orders.Any(order => order.Id == fill.OrderId))
            .Select(fill => new { fill.OrderId, fill.At })
            .ToListAsync(cancellationToken);

        var inAnotherAsset = await context.Set<Funding>()
            .AsNoTracking()
            .AnyAsync(row => row.PositionId == positionId && row.Asset != position.QuoteAsset, cancellationToken);

        // R: what was risked when the position was opened, which its entry keeps. The stop-loss
        // of the position is moved, and what it holds grows less as it is closed in parts.
        var entry = rows.FirstOrDefault(order => order.Purpose == OrderPurpose.Entry);
        var risk = entry is { AveragePrice: { } enteredAt, StopLoss: { } stopLoss, ExecutedQuantity: > 0m }
            ? Worked(() => AgentResults.Rounded(Math.Abs(enteredAt - stopLoss) * entry.ExecutedQuantity))
            : null;

        var net = made - fees + funding;
        var inR = risk is > 0m ? Worked(() => AgentResults.Rounded(net / risk.Value)) : null;

        // Held from the first execution of its entry to the last execution that closed it, by
        // the clock of the exchange. A position that no order of the agent closed was held
        // until that was found, and so was one whose executions are not stored.
        var from = executions.Where(fill => entries.Contains(fill.OrderId)).Select(fill => (DateTimeOffset?)fill.At).Min() ?? position.OpenedAt;
        var exited = executions.Where(fill => !entries.Contains(fill.OrderId)).Select(fill => (DateTimeOffset?)fill.At).Max();
        var to = position.ExitReason == ExitReason.ClosedAtExchange ? closedAt : exited ?? closedAt;

        var complete = priced
            && valued
            && !inAnotherAsset
            && position.MadeAtExchange is null
            && position.FundingDueAt is null
            && rows.All(order => !OrderStatuses.IsLive(order.Status) && (order.ExecutedQuantity <= 0m || order.FillsRecorded));

        return (position, new PositionResult(
            position.Id,
            position.UserId,
            position.QuoteAsset,
            made,
            fees,
            funding,
            net,
            risk,
            inR,
            Math.Max((long)Math.Floor((to - from).TotalSeconds), 0L),
            complete,
            clock.GetUtcNow()));
    }

    /// <summary>Whether a position has its result, asked with a context of its own after a write was refused.</summary>
    private async Task<bool> IsRecordedAsync(Guid positionId)
    {
        await using var context = await contexts.CreateDbContextAsync(CancellationToken.None);

        return await context.Set<PositionResult>().AsNoTracking().AnyAsync(row => row.PositionId == positionId, CancellationToken.None);
    }

    /// <summary>
    /// Has the loss limits of an agent evaluated, which records its equity. What cannot be
    /// evaluated is said once, as an error, and takes nothing from the results that were
    /// recorded.
    /// </summary>
    private async Task EvaluateAsync(Guid agentId, CancellationToken cancellationToken)
    {
        try
        {
            if (await agents.FindAsync(agentId, cancellationToken) is { } agent)
            {
                await limits.EvaluateAsync(agent, cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The loss limits of agent {AgentId} could not be evaluated as the result of its position was recorded", agentId);
        }
    }

    // Numbers that cannot be worked out with are no numbers of a market: the amount is not known.
    private static decimal? Worked(Func<decimal> amount)
    {
        try
        {
            return amount();
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
