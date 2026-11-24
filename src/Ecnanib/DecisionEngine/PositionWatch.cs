using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Watches one position of the Futures market: it checks the margin of an open position, and
/// tells its user when the position approaches liquidation (FR-BIN-F5), and it reads what the
/// position received and paid as funding, which is stored for the position (FR-BIN-F4). See
/// "Funding" and "The margin of a position" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It only reads the exchange.</b> It is handed the operations of the Exchange Gateway that
/// read, and none that places an order or sets the margin of a symbol: those are of
/// <see cref="AgentOrders"/> alone. The gateway has no call that adds margin to a position, so
/// the service never adds any (FR-BIN-F5): a user who is told that a position approaches
/// liquidation closes or reduces it, or adds margin at the exchange.
/// </para>
/// <para>
/// <b>The funding of a position is read when it is due,</b> and not with every run: the read is
/// the heaviest call that the service makes. It is due a while after the position was opened,
/// then a while after each funding time, which the exchange says with the mark price, and once
/// more a while after the position was closed. The while is the time in which the exchange is
/// taken to have written what it paid. What is read starts at the newest funding that is stored,
/// so funding that the exchange wrote later than that is read the next time, and a row that is
/// read twice is stored once.
/// </para>
/// <para>
/// <b>A stored key is asked for with each position,</b> handed to the Exchange Gateway with the
/// calls of that position, and kept nowhere (FR-KEY-9). Without one, nothing is asked.
/// </para>
/// </remarks>
internal sealed class PositionWatch(
    IExchangeAdapter account,
    IExchangeMarketData market,
    IKeyHandOver keys,
    IKeyValidation keyChecks,
    StoredOrders stored,
    INotifications notifications,
    TradingSettings trading,
    BinanceSettings binance,
    TimeProvider clock,
    ILogger<PositionWatch> logger)
{
    /// <summary>
    /// How long after a funding time, and after a position was closed, the funding of the
    /// position is read: the time in which the exchange is taken to have written what the
    /// position received or paid.
    /// </summary>
    public static readonly TimeSpan FundingWrittenWithin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The time in which the user of an agent is told once that its position approaches
    /// liquidation. The margin is checked every few minutes, and a position that approaches
    /// liquidation may do so for hours: one notification for each check would leave no room for
    /// the others.
    /// </summary>
    public static readonly TimeSpan WarnedEvery = TimeSpan.FromHours(1);

    /// <summary>Whether the funding of a position is to be read now.</summary>
    public bool FundingIsDue(Position position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return position.FundingDueAt is { } dueAt && clock.GetUtcNow() >= (position.ClosedAt ?? dueAt) + FundingWrittenWithin;
    }

    /// <summary>
    /// Watches a position: the margin of one that is open is checked, and the funding of one
    /// that is due is read, when this is its turn to be.
    /// </summary>
    /// <param name="position">The position, as it is stored.</param>
    /// <param name="readsFunding">Whether its funding is read with this run, when it is due. A run reads the funding of so many positions and no more.</param>
    /// <param name="cancellationToken">Canceled when the run is stopped. What was read by then is stored.</param>
    public async Task WatchAsync(Position position, bool readsFunding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);

        var key = await keys.ExchangeKeyAsync(position.UserId, position.Mode, cancellationToken);

        if (key.Outcome != KeyHandOverOutcome.Handed)
        {
            // Without a key nothing is asked. What was not read is read when there is one.
            return;
        }

        // The exchange refuses the key, which it does for the next call as well. Or the account
        // does not hold the position as the records say: it was closed at the exchange, and
        // what settles the orders has not seen that yet. Its funding is read once it has, up
        // to the execution that closed it.
        if (position.ClosedAt is null && !await CheckMarginAsync(position, key.ApiKey!, key.SecretKey!, cancellationToken))
        {
            return;
        }

        if (readsFunding && FundingIsDue(position))
        {
            await ReadFundingAsync(position, key.ApiKey!, key.SecretKey!, cancellationToken);
        }
    }

    /// <summary>
    /// Whether a position approaches liquidation: no more than a share of the way from the
    /// price that it was entered at to the price that it is liquidated at is left between the
    /// mark price and the liquidation price. A mark price that has reached the liquidation price
    /// is one that approaches it, and so is a liquidation price that is not on the losing side
    /// of the entry, where there is no such way.
    /// </summary>
    /// <param name="side">The side of the position.</param>
    /// <param name="entry">The price that it was entered at.</param>
    /// <param name="mark">The mark price.</param>
    /// <param name="liquidation">The price that the exchange liquidates it at.</param>
    /// <param name="percent">The share of the way, in percent, at which it approaches liquidation.</param>
    /// <param name="left">What is left of the way, in percent of it, to one place: nothing where there is no way, or none of it is left.</param>
    public static bool Approaches(PositionSide side, decimal entry, decimal mark, decimal liquidation, int percent, out decimal left)
    {
        var way = side == PositionSide.Long ? entry - liquidation : liquidation - entry;
        var remains = side == PositionSide.Long ? mark - liquidation : liquidation - mark;

        left = way > 0m && remains > 0m ? Math.Round(remains / way * 100m, 1, MidpointRounding.AwayFromZero) : 0m;

        // Held without dividing, so that a position at the share itself approaches liquidation.
        return way <= 0m || remains * 100m <= way * percent;
    }

    /// <summary>
    /// Checks the margin of an open position, and tells its user when it approaches liquidation
    /// (FR-BIN-F5). Nothing is corrected here: a position that the account does not hold as the
    /// records say is said, and left to what reconciles the agent, which corrects the records
    /// (FR-BIN-7): see <see cref="IPlanOrders.ReconcileAsync"/>.
    /// </summary>
    /// <returns>
    /// Whether the position is one to go on with: the exchange took the key, and the account
    /// holds the position, as far as the exchange said.
    /// </returns>
    private async Task<bool> CheckMarginAsync(Position position, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        var read = await account.GetFuturesPositionAsync(position.UserId, position.Mode, apiKey, secretKey, position.Symbol, cancellationToken);

        if (read.Outcome != ExchangeCallOutcome.Succeeded)
        {
            logger.LogWarning(
                "The margin of the position of agent {AgentId} in {Symbol} could not be read: {ExchangeCallOutcome}",
                position.AgentId,
                position.Symbol,
                read.Outcome);

            if (read.Outcome == ExchangeCallOutcome.AuthenticationFailed)
            {
                await RecheckKeyAsync(position.UserId, position.Mode);

                return false;
            }

            return true;
        }

        if (position.Side == PositionSide.Long ? read.Quantity <= 0m : read.Quantity >= 0m)
        {
            // Most often a position that its stop-loss or its take-profit closed a moment ago,
            // which what settles the orders sees with its next run. Whatever it is, it is left
            // to that, and to what reconciles the agent, which corrects the records.
            logger.LogInformation(
                "The account of agent {AgentId} does not hold the {PositionSide} position in {Symbol} that the records say, as the exchange says it: its margin is not checked, and its funding is not read",
                position.AgentId,
                position.Side,
                position.Symbol);

            return false;
        }

        // The prices of a position that the account holds are there. A liquidation price is not
        // when the exchange names none: there is nothing to approach.
        var entry = read.EntryPrice!.Value;
        var mark = read.MarkPrice!.Value;

        if (read.LiquidationPrice is not { } liquidation)
        {
            logger.LogDebug(
                "The margin of the position of agent {AgentId} in {Symbol} was checked: the exchange names no liquidation price",
                position.AgentId,
                position.Symbol);

            return true;
        }

        if (!Approaches(position.Side, entry, mark, liquidation, trading.LiquidationWarningPercent, out var left))
        {
            logger.LogDebug(
                "The margin of the position of agent {AgentId} in {Symbol} was checked: {WayLeftPercent} percent of the way from its entry to its liquidation price is left",
                position.AgentId,
                position.Symbol,
                left);

            return true;
        }

        logger.LogWarning(
            "The position of agent {AgentId} in {Symbol} approaches liquidation: {WayLeftPercent} percent of the way from its entry to its liquidation price is left",
            position.AgentId,
            position.Symbol,
            left);

        try
        {
            await notifications.RaiseOnceAsync(
                position.UserId,
                NotificationType.LiquidationWarning,
                position.AgentId,
                position.Mode,
                WarningOf(position, mark, liquidation),
                WarnedEvery,
                CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(
                error,
                "The user {UserId} could not be told that the position of agent {AgentId} approaches liquidation",
                position.UserId,
                position.AgentId);
        }

        return true;
    }

    /// <summary>
    /// Reads what a position received and paid as funding since the newest of it that is stored,
    /// stores it, and writes when its funding is read next (FR-BIN-F4): at the next funding time
    /// that the exchange says, for an open position, and never again for a closed one. A read
    /// that does not succeed changes nothing, so the next run reads again.
    /// </summary>
    private async Task ReadFundingAsync(Position position, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        DateTimeOffset? next = null;

        // An open position is read again after the next funding time. Without that time
        // nothing is read: it would be read again with every run.
        if (position.ClosedAt is null)
        {
            next = await NextFundingAsync(position, cancellationToken);

            if (next is null)
            {
                return;
            }
        }

        var (from, to) = await stored.FundingWindowAsync(position, cancellationToken);

        IReadOnlyList<ExchangeFunding> said = [];
        var more = false;

        // A last moment before the first is a time in which nothing can have been paid.
        if (to >= from)
        {
            var read = await account.GetFundingAsync(
                position.UserId, position.Mode, apiKey, secretKey, position.Symbol, from, to, cancellationToken);

            if (read.Outcome != ExchangeCallOutcome.Succeeded)
            {
                logger.LogWarning(
                    "The funding of the position of agent {AgentId} in {Symbol} could not be read: {ExchangeCallOutcome}",
                    position.AgentId,
                    position.Symbol,
                    read.Outcome);

                if (read.Outcome == ExchangeCallOutcome.AuthenticationFailed)
                {
                    await RecheckKeyAsync(position.UserId, position.Mode);
                }

                return;
            }

            said = read.Fundings;
            more = read.More;
        }

        List<Funding> rows =
        [
            .. said
                .Where(funding => funding.At >= from && funding.At <= to)
                .Select(funding => new Funding(
                    Guid.CreateVersion7(), position.Id, position.UserId, funding.TransactionId, funding.Amount, funding.Asset, funding.At)),
        ];

        // An answer of as many rows as one answer holds may not be all: the position stays due,
        // and the next run reads on from the newest row that is stored now.
        var written = await stored.RecordFundingAsync(position, rows, more ? position.FundingDueAt : next, CancellationToken.None);

        if (more)
        {
            logger.LogWarning(
                "The funding of the position of agent {AgentId} in {Symbol} is more than one answer of the exchange holds: the rest is read with the next run",
                position.AgentId,
                position.Symbol);
        }

        if (written is > 0)
        {
            logger.LogInformation(
                "{Fundings} rows of funding were stored for the position of agent {AgentId} in {Symbol}",
                written,
                position.AgentId,
                position.Symbol);
        }
    }

    /// <summary>
    /// The next funding time of the symbol of a position, as the exchange says it with the mark
    /// price, or null when it does not say one that is ahead: the read did not succeed, the time
    /// that it gave has passed, or the symbol is no longer one that the service reads market
    /// data of. The funding of such a position is read when the position is closed.
    /// </summary>
    private async Task<DateTimeOffset?> NextFundingAsync(Position position, CancellationToken cancellationToken)
    {
        if (!binance.Symbols.Contains(position.Symbol))
        {
            return null;
        }

        // The gateway shares the read among everyone that needs it (FR-BIN-3).
        var read = await market.GetMarkPriceAsync(position.Mode, position.Symbol, cancellationToken);

        return read.Outcome == ExchangeCallOutcome.Succeeded && read.NextFundingAt is { } next && next > clock.GetUtcNow() ? next : null;
    }

    /// <summary>
    /// Has the credential of a user checked again, after the exchange answered a call as it
    /// answers a wrong credential: the check decides whether it is (FR-KEY-3).
    /// </summary>
    private async Task RecheckKeyAsync(Guid userId, TradingMode mode)
    {
        try
        {
            await keyChecks.RevalidateExchangeKeyAsync(userId, mode, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The Binance key of user {UserId} for {TradingMode} could not be checked again", userId, mode);
        }
    }

    /// <summary>What the user of a position is told when it approaches liquidation.</summary>
    private static string WarningOf(Position position, decimal mark, decimal liquidation)
    {
        var side = position.Side == PositionSide.Long ? "long" : "short";
        var left = position.Side == PositionSide.Long ? mark - liquidation : liquidation - mark;

        var where = left > 0m
            ? $"its mark price is {Written.Number(mark)}, and Binance liquidates it at {Written.Number(liquidation)}, " +
                $"which is {Written.Number(left / mark * 100m, 2)}% away"
            : $"its mark price is {Written.Number(mark)}, and has reached the price that Binance liquidates it at, {Written.Number(liquidation)}";

        return $"The {side} position in {position.Symbol} ({position.Mode}) approaches liquidation: {where}. " +
            "No margin is added to a position for you: close the position or reduce it, or add margin to it at Binance. " +
            "This is said once in an hour for an agent.";
    }
}
