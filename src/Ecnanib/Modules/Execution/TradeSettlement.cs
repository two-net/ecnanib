using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using ExchangeSide = Ecnanib.Modules.ExchangeAdapter.Contracts.OrderSide;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Settles a closed trade from Binance (FR-EXE-07): its fills and their fees in the quote asset, converted at the fill's
/// minute when charged in another asset; the price of what was closed outside Ecnanib, from the account's own trades;
/// Futures funding, shared among the agent's trades by what each held; and the excursions, from candles. Then its
/// average prices, P&amp;L and R-multiple are worked out again from those. Every read is at normal priority, and nothing
/// holds the agent's lock, so protective work never waits for it. A trade is written once, whole, or not at all, and
/// tried again later. See The trade journal in docs/architecture.md.
/// </summary>
internal sealed class TradeSettlement(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    AgentSessions sessions,
    IExchangeAccount accounts,
    IExchangeMarketData marketData,
    ExecutionObservers observers,
    TimeProvider time,
    ILogger<TradeSettlement> logger)
{
    /// <summary>At most this many of an agent's trades a pass, so a backlog can't spend the weight cycles need.</summary>
    public const int TradesPerPass = 5;

    /// <summary>
    /// How long after a trade closed settling keeps trying, whatever stops it, such as a key that can't be used or an
    /// answer Binance keeps refusing, before it settles the trade from its orders.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    /// <summary>How far back from a close outside Ecnanib the account's trades are read.</summary>
    public static readonly TimeSpan OutsideWindow = TimeSpan.FromHours(24);

    /// <summary>How far a funding rate's time may be from the payment's.</summary>
    private static readonly TimeSpan RateMatch = TimeSpan.FromMinutes(1);

    /// <summary>
    /// An open Futures trade's funding is read after each of these UTC boundaries, which every funding interval
    /// Binance uses but one hour falls on, so an hourly symbol's shows up to four hours late.
    /// </summary>
    public static readonly TimeSpan FundingStep = TimeSpan.FromHours(4);

    /// <summary>
    /// How long after a boundary an open trade's funding is read, at the least: Binance books the payment just after,
    /// and the candle closes that every timeframe shares are over by then.
    /// </summary>
    public static readonly TimeSpan FundingDelay = TimeSpan.FromMinutes(5);

    /// <summary>Agents' reads are spread over this much after <see cref="FundingDelay"/>, by agent, so they don't all come at once.</summary>
    public static readonly TimeSpan FundingSpread = TimeSpan.FromMinutes(30);

    /// <summary>How far before what was read last an open trade's funding is read again, in case Binance booked one late.</summary>
    private static readonly TimeSpan FundingOverlap = TimeSpan.FromHours(1);

    /// <summary>Binance's code for a symbol it doesn't list.</summary>
    private const int UnknownSymbol = -1121;

    /// <summary>The agent's closed trades that are due, oldest first. Returns how many were settled.</summary>
    public async Task<int> SettleDueAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        List<TradeRow> due;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            due = await context.Set<TradeRow>()
                .AsNoTracking()
                .Where(trade => trade.UserId == userId && trade.AgentId == agentId && trade.Status == TradeStatus.Closed
                    && trade.SettledAt == null && (trade.SettleAfter == null || trade.SettleAfter <= now))
                .OrderBy(trade => trade.ClosedAt)
                .Take(TradesPerPass)
                .ToListAsync(cancellationToken);
        }

        var settled = 0;

        foreach (var group in due.GroupBy(trade => (trade.CredentialId, trade.Market, trade.Environment, trade.Symbol)))
        {
            AgentSession session;

            try
            {
                // The trade's own key and pair: the agent's newest may differ once it had no orders.
                session = await sessions.OpenAsync(userId, agentId, group.Key.CredentialId, group.Key.Market, group.Key.Environment, group.Key.Symbol, cancellationToken);
            }
            catch (AgentSessionException exception)
            {
                foreach (var trade in group)
                {
                    settled += await FailedAsync(trade, now, $"its key or pair can't be used: {exception.Failure}") ? 1 : 0;
                }

                continue;
            }

            using (session)
            {
                var pass = new Pass();

                foreach (var trade in group)
                {
                    try
                    {
                        await SettleAsync(session, trade, pass, cancellationToken);
                        settled++;
                    }
                    catch (ExchangeException exception)
                    {
                        settled += await FailedAsync(trade, now, $"Binance couldn't give what it needs: {exception.Failure}") ? 1 : 0;
                    }
                }
            }
        }

        return settled;
    }

    /// <summary>
    /// The latest <see cref="FundingStep"/> boundary whose funding the agent's open Futures trades are due to have read
    /// by <paramref name="now"/>, or null while it's still within the boundary's delay and the agent's share of the
    /// spread.
    /// </summary>
    public static DateTimeOffset? FundingDue(Guid agentId, DateTimeOffset now)
    {
        var boundary = TradeMath.Floor(now, FundingStep);
        var spread = TimeSpan.FromTicks((agentId.GetHashCode() & int.MaxValue) % FundingSpread.Ticks);

        return now >= boundary + FundingDelay + spread ? boundary : null;
    }

    /// <summary>
    /// What the agent's open trades have cost so far, so their unrealized P&amp;L is net of it (FR-AGT-11): an entry's
    /// fills and their fees, once the entry has ended, and on Futures the funding paid or received, after each
    /// <see cref="FundingStep"/> boundary. Each read is once: the fills when the entry ended, and the funding since the
    /// last read, with an hour's overlap that is skipped by ID. Settling reads both again, whole, and replaces these. A
    /// few trades a pass, like settling. Returns how many trades were read.
    /// </summary>
    public async Task<int> AccrueOpenAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = FundingDue(agentId, now);
        List<TradeRow> open;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            open = await context.Set<TradeRow>()
                .AsNoTracking()
                .Where(trade => trade.UserId == userId && trade.AgentId == agentId && trade.Status == TradeStatus.Open
                    && ((trade.EntryFeesReadAt == null && context.Set<OrderRow>().Any(entry => entry.Id == trade.EntryId && entry.Resolved))
                        || (due != null && trade.Market == Market.Futures && (trade.FundingReadTo ?? trade.OpenedAt) < due)))
                .OrderBy(trade => trade.OpenedAt)
                .Take(TradesPerPass)
                .ToListAsync(cancellationToken);
        }

        var accrued = 0;

        foreach (var group in open.GroupBy(trade => (trade.CredentialId, trade.Market, trade.Environment, trade.Symbol)))
        {
            AgentSession session;

            try
            {
                session = await sessions.OpenAsync(userId, agentId, group.Key.CredentialId, group.Key.Market, group.Key.Environment, group.Key.Symbol, cancellationToken);
            }
            catch (AgentSessionException exception)
            {
                // Settling reads it all once the trade closes, and the key's own checks deal with a key that can't be used.
                logger.LogInformation("An open trade's fees and funding can't be read yet, because its key or pair can't be used: {Failure}", exception.Failure);
                continue;
            }

            using (session)
            {
                var pass = new Pass();

                foreach (var trade in group)
                {
                    try
                    {
                        accrued += await AccrueAsync(session, trade, due, pass, now, cancellationToken) ? 1 : 0;
                    }
                    catch (ExchangeException exception)
                    {
                        logger.LogInformation("Open trade {TradeId}'s fees and funding couldn't be read, so they're read next time: {Failure}", trade.Id, exception.Failure);
                    }
                }
            }
        }

        return accrued;
    }

    /// <summary>One open trade's reads that are due. True when anything was written.</summary>
    private async Task<bool> AccrueAsync(AgentSession session, TradeRow trade, DateTimeOffset? due, Pass pass, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OrderRow entry;
        List<ProtectionRow> parts;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            entry = await context.Set<OrderRow>().AsNoTracking().SingleAsync(row => row.Id == trade.EntryId, cancellationToken);
            parts = await context.Set<ProtectionRow>().AsNoTracking().Where(row => row.EntryId == trade.EntryId).ToListAsync(cancellationToken);
        }

        var fills = new List<FillRow>();
        var readFills = trade.EntryFeesReadAt is null && entry.Resolved;

        if (readFills && entry.ExchangeOrderId is { } exchangeOrderId)
        {
            var orderTrades = await accounts.GetOrderTradesAsync(session.Market, session.Key, trade.Symbol, exchangeOrderId, RequestPriority.Normal, cancellationToken);

            foreach (var fill in orderTrades)
            {
                fills.Add(new FillRow
                {
                    Id = Guid.NewGuid(),
                    TradeId = trade.Id,
                    OrderId = entry.Id,
                    Source = FillSource.Agent,
                    ExchangeTradeId = fill.TradeId,
                    ExchangeOrderId = fill.OrderId,
                    Side = entry.Side,
                    Price = fill.Price,
                    Quantity = fill.Quantity,
                    Fee = fill.Commission,
                    FeeAsset = fill.CommissionAsset,
                    FeeQuote = await FeeInQuoteAsync(session, trade, fill.Commission, fill.CommissionAsset, fill.Price, fill.Time, pass, cancellationToken),
                    Maker = fill.Maker,
                    FilledAt = fill.Time,
                    CreatedAt = now,
                });
            }
        }

        var funding = new List<FundingPaymentRow>();
        var readFunding = due is { } boundary && trade.Market == Market.Futures && (trade.FundingReadTo ?? trade.OpenedAt) < boundary;

        if (readFunding)
        {
            var from = trade.FundingReadTo is { } read && read - FundingOverlap > trade.OpenedAt ? read - FundingOverlap : trade.OpenedAt;
            funding = await FundingAsync(session, trade, TradeMath.Leaves(parts), from, now, withRates: false, now, cancellationToken);
        }

        if (!readFills && !readFunding)
        {
            return false;
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var recordedFills = await context.Set<FillRow>()
                .Where(fill => fill.TradeId == trade.Id && fill.Source == FillSource.Agent)
                .Select(fill => fill.ExchangeTradeId)
                .ToListAsync(cancellationToken);
            var recordedPayments = await context.Set<FundingPaymentRow>()
                .Where(payment => payment.TradeId == trade.Id)
                .Select(payment => payment.ExchangeTransactionId)
                .ToListAsync(cancellationToken);

            context.AddRange(fills.Where(fill => !recordedFills.Contains(fill.ExchangeTradeId)));
            context.AddRange(funding.Where(payment => !recordedPayments.Contains(payment.ExchangeTransactionId)));

            var stored = await context.Set<TradeRow>().SingleAsync(row => row.Id == trade.Id, cancellationToken);
            stored.EntryFeesReadAt = readFills ? now : stored.EntryFeesReadAt;
            stored.FundingReadTo = readFunding ? now : stored.FundingReadTo;
            stored.UpdatedAt = now;

            observers.AfterCommit(context, trade.UserId, trade.AgentId);
            await context.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Read open trade {TradeId}'s costs so far: {Fills} entry fills and {Payments} funding payments", trade.Id, fills.Count, funding.Count);

        return true;
    }

    private async Task SettleAsync(AgentSession session, TradeRow trade, Pass pass, CancellationToken cancellationToken)
    {
        OrderRow entry;
        List<OrderRow> legs;
        List<ProtectionRow> parts;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            entry = await context.Set<OrderRow>().AsNoTracking().SingleAsync(row => row.Id == trade.EntryId, cancellationToken);
            legs = await context.Set<OrderRow>().AsNoTracking().Where(row => row.EntryId == trade.EntryId).ToListAsync(cancellationToken);
            parts = await context.Set<ProtectionRow>().AsNoTracking().Where(row => row.EntryId == trade.EntryId).ToListAsync(cancellationToken);
        }

        var now = time.GetUtcNow();
        var leaves = TradeMath.Leaves(parts);
        var fills = new List<FillRow>();
        var unfilled = new List<(decimal Price, decimal Quantity)>();

        foreach (var order in legs.Where(leg => leg.Leg != OrderLeg.Entry).Prepend(entry))
        {
            var approximate = TradeBook.ExecutedWithoutQuantity(trade, order, parts);

            if (order.ExecutedQuantity <= 0m && !approximate)
            {
                continue;
            }

            var exchangeOrderId = await FillsOrderIdAsync(session, order, pass, cancellationToken);
            var orderTrades = exchangeOrderId is { } id
                ? await accounts.GetOrderTradesAsync(session.Market, session.Key, trade.Symbol, id, RequestPriority.Normal, cancellationToken)
                : [];

            if (orderTrades.Count == 0)
            {
                logger.LogWarning(
                    "Binance has no fills of order {ClientOrderId}, so trade {TradeId} takes its price from the order, and its fee is unknown",
                    order.ClientOrderId,
                    trade.Id);

                if (order.Id != entry.Id)
                {
                    unfilled.AddRange(TradeBook.ExitsFromRecords(trade, parts, [order]));
                }

                continue;
            }

            foreach (var fill in orderTrades)
            {
                fills.Add(new FillRow
                {
                    Id = Guid.NewGuid(),
                    TradeId = trade.Id,
                    OrderId = order.Id,
                    Source = FillSource.Agent,
                    ExchangeTradeId = fill.TradeId,
                    ExchangeOrderId = fill.OrderId,
                    Side = order.Side,
                    Price = fill.Price,
                    Quantity = fill.Quantity,
                    Fee = fill.Commission,
                    FeeAsset = fill.CommissionAsset,
                    FeeQuote = await FeeInQuoteAsync(session, trade, fill.Commission, fill.CommissionAsset, fill.Price, fill.Time, pass, cancellationToken),
                    Maker = fill.Maker,
                    FilledAt = fill.Time,
                    CreatedAt = now,
                });
            }
        }

        fills.AddRange(await OutsideAsync(session, trade, leaves, pass, now, cancellationToken));

        var funding = trade.Market == Market.Futures
            ? await FundingAsync(session, trade, leaves, trade.OpenedAt, trade.ClosedAt!.Value, withRates: true, now, cancellationToken)
            : [];
        var excursions = await ExcursionsAsync(trade, cancellationToken);

        Settle(trade, entry, fills, unfilled, funding, excursions, now);

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // What was read while it was open goes, because this has read it all again, whole.
            await context.Set<FillRow>().Where(fill => fill.TradeId == trade.Id).ExecuteDeleteAsync(cancellationToken);
            await context.Set<FundingPaymentRow>().Where(payment => payment.TradeId == trade.Id).ExecuteDeleteAsync(cancellationToken);

            context.Update(trade);
            context.AddRange(fills);
            context.AddRange(funding);
            observers.AfterCommit(context, trade.UserId, trade.AgentId);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Trade {TradeId} settled: {RealizedPnl} {QuoteAsset} after {Fees} in fees and {Funding} in funding, {RMultiple} R",
            trade.Id,
            trade.RealizedPnl,
            trade.QuoteAsset,
            trade.Fees,
            trade.Funding,
            trade.RMultiple);
    }

    /// <summary>Works the trade's figures out again from what Binance gave.</summary>
    private static void Settle(
        TradeRow trade,
        OrderRow entry,
        List<FillRow> fills,
        List<(decimal Price, decimal Quantity)> unfilled,
        List<FundingPaymentRow> funding,
        (decimal Adverse, decimal Favorable)? excursions,
        DateTimeOffset now)
    {
        var entryFills = fills.Where(fill => fill.OrderId == entry.Id).ToList();
        var exits = fills.Where(fill => fill.OrderId != entry.Id).Select(fill => (fill.Price, fill.Quantity)).Concat(unfilled).ToList();

        trade.EntryPrice = TradeMath.Average(entryFills.Select(fill => (fill.Price, fill.Quantity))) ?? trade.EntryPrice;
        trade.ExitQuantity = exits.Sum(exit => exit.Quantity);
        trade.ExitPrice = TradeMath.Average(exits);
        trade.UnpricedQuantity = Math.Max(0m, trade.Quantity - trade.ExitQuantity);
        trade.Fees = fills.Count > 0 ? fills.Sum(fill => fill.FeeQuote ?? 0m) : null;
        trade.Funding = trade.Market == Market.Futures ? funding.Sum(payment => payment.Amount) : 0m;

        var gross = TradeMath.Gross(trade.Side, trade.EntryPrice, exits.Sum(exit => exit.Price * exit.Quantity), trade.ExitQuantity);

        trade.RealizedPnl = gross - (trade.Fees ?? 0m) + (trade.Funding ?? 0m);
        trade.RMultiple = TradeMath.RMultiple(trade.RealizedPnl.Value, trade.EntryPrice, trade.InitialStop, trade.ExitQuantity);
        trade.AdverseExcursion = excursions?.Adverse;
        trade.FavorableExcursion = excursions?.Favorable;
        trade.SettledAt = now;
        trade.SettleAfter = null;
        trade.UpdatedAt = now;
    }

    /// <summary>
    /// The ID Binance files an order's fills under: its own, or a Futures stop's or target's the order it became, read
    /// once when an order row from before the journal doesn't have it.
    /// </summary>
    private async Task<long?> FillsOrderIdAsync(AgentSession session, OrderRow order, Pass pass, CancellationToken cancellationToken)
    {
        if (!(order.Market == Market.Futures && order.Type is OrderKind.StopMarket or OrderKind.TakeProfitMarket))
        {
            return order.ExchangeOrderId;
        }

        if (order.TriggeredOrderId is { } known)
        {
            return known;
        }

        if (pass.Triggered.TryGetValue(order.Id, out var found))
        {
            return found;
        }

        ExchangeOrder? read;

        try
        {
            read = await accounts.GetOrderAsync(Market.Futures, session.Key, ProtectionKeeper.Reference(order), RequestPriority.Normal, cancellationToken);
        }
        catch (ExchangeException exception) when (exception.Failure == ExchangeFailure.Rejected)
        {
            // Binance doesn't keep a conditional order for ever, and may answer for a missing one with a code of its own.
            read = null;
        }

        pass.Triggered[order.Id] = read?.TriggeredOrderId;

        return read?.TriggeredOrderId;
    }

    /// <summary>
    /// A commission in the quote asset. Another asset, such as BNB, is converted at the close of the one-minute Spot
    /// candle the fill fell in: its pair with the quote asset, or failing that the inverse. Null when Binance lists
    /// neither, which is logged.
    /// </summary>
    /// <exception cref="ExchangeException">Binance couldn't give the price now, so the trade is settled later.</exception>
    private async Task<decimal?> FeeInQuoteAsync(
        AgentSession session,
        TradeRow trade,
        decimal fee,
        string asset,
        decimal price,
        DateTimeOffset at,
        Pass pass,
        CancellationToken cancellationToken)
    {
        if (fee == 0m)
        {
            return 0m;
        }

        if (TradeMath.FeeInQuote(fee, asset, trade.BaseAsset, trade.QuoteAsset, price) is { } direct)
        {
            return direct;
        }

        var minute = TradeMath.Floor(at, TimeSpan.FromMinutes(1));

        if (!pass.Rates.TryGetValue((asset, minute), out var rate))
        {
            rate = await CloseAsync(session, asset + trade.QuoteAsset, minute, cancellationToken) is { } close
                ? close
                : await CloseAsync(session, trade.QuoteAsset + asset, minute, cancellationToken) is > 0m and var inverse ? 1m / inverse : null;

            pass.Rates[(asset, minute)] = rate;

            if (rate is null)
            {
                logger.LogWarning(
                    "Binance has no {Asset} price in {QuoteAsset} at {Minute:O}, so a fee of trade {TradeId} can't be converted",
                    asset,
                    trade.QuoteAsset,
                    minute,
                    trade.Id);
            }
        }

        return rate is { } value ? fee * value : null;
    }

    /// <summary>The close of one Spot pair's one-minute candle. Null when Binance doesn't list the pair, or had no candle then.</summary>
    private async Task<decimal?> CloseAsync(AgentSession session, string symbol, DateTimeOffset minute, CancellationToken cancellationToken)
    {
        // An asset Binance names oddly can't make a pair it lists.
        if (symbol.Length > OrderRow.SymbolMaxLength || !symbol.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character)))
        {
            return null;
        }

        try
        {
            var candles = await marketData.GetCandlesAsync(
                Market.Spot, session.Environment, symbol, CandleInterval.OneMinute, minute, minute, 1, cancellationToken);

            return candles.Count > 0 ? candles[0].Close : null;
        }
        catch (ExchangeException exception) when (exception is { Failure: ExchangeFailure.Rejected, Code: UnknownSymbol })
        {
            return null;
        }
    }

    /// <summary>
    /// Prices the trade's parts that were closed outside Ecnanib (FR-EXE-06) from the account's own trades on the closing
    /// side, leaving out the agent's orders, newest first, up to each part's quantity. Each of Binance's trades is used
    /// once among all the agent's trades, so two parts closed by the same sale don't both claim it. What no trade covers
    /// stays unpriced.
    /// </summary>
    private async Task<List<FillRow>> OutsideAsync(
        AgentSession session, TradeRow trade, List<ProtectionRow> leaves, Pass pass, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outside = leaves
            .Where(leaf => leaf is { Status: ProtectionStatus.Ended, EndReason: ProtectionEnd.ClosedOutside })
            .OrderBy(TradeMath.EndOf)
            .ThenBy(leaf => leaf.Sequence)
            .ToList();

        if (outside.Count == 0)
        {
            return [];
        }

        var earliest = outside.Min(leaf => TradeMath.EndOf(leaf) - OutsideWindow);
        var agentOrders = await AgentOrderIdsAsync(session, trade, earliest, pass, cancellationToken);
        var fills = new Dictionary<long, FillRow>();

        // What this trade takes, which isn't saved yet. The agent's other trades' is in the database.
        var taken = new Dictionary<long, decimal>();

        foreach (var group in outside.GroupBy(TradeMath.EndOf))
        {
            var to = group.Key;
            var from = Max(group.Min(leaf => leaf.OpenedAt), to - OutsideWindow);

            if (!pass.Outside.TryGetValue((from, to), out var candidates))
            {
                candidates = await accounts.GetTradesAsync(session.Market, session.Key, trade.Symbol, from, to, RequestPriority.Normal, cancellationToken);
                pass.Outside[(from, to)] = candidates;
            }

            // A long is closed by selling, and a short by buying.
            var closing = candidates
                .Where(candidate => candidate.Buyer == (trade.Side == PositionSide.Short) && !agentOrders.Contains(candidate.OrderId))
                .OrderByDescending(candidate => candidate.Time)
                .ThenByDescending(candidate => candidate.TradeId)
                .ToList();

            var used = await UsedAsync(trade, closing.Select(candidate => candidate.TradeId).ToList(), cancellationToken);

            foreach (var leaf in group)
            {
                var need = leaf.Quantity;

                foreach (var candidate in closing)
                {
                    var available = candidate.Quantity - used.GetValueOrDefault(candidate.TradeId) - taken.GetValueOrDefault(candidate.TradeId);

                    if (need <= 0m)
                    {
                        break;
                    }

                    if (available <= 0m)
                    {
                        continue;
                    }

                    var take = Math.Min(available, need);
                    need -= take;
                    taken[candidate.TradeId] = taken.GetValueOrDefault(candidate.TradeId) + take;

                    var fee = Math.Round(candidate.Commission * take / candidate.Quantity, 8, MidpointRounding.ToZero);
                    var feeQuote = await FeeInQuoteAsync(session, trade, fee, candidate.CommissionAsset, candidate.Price, candidate.Time, pass, cancellationToken);

                    fills[candidate.TradeId] = fills.TryGetValue(candidate.TradeId, out var earlier)
                        ? Merge(earlier, take, fee, feeQuote)
                        : new FillRow
                        {
                            Id = Guid.NewGuid(),
                            TradeId = trade.Id,
                            OrderId = null,
                            Source = FillSource.Outside,
                            ExchangeTradeId = candidate.TradeId,
                            ExchangeOrderId = candidate.OrderId,
                            Side = candidate.Buyer ? ExchangeSide.Buy : ExchangeSide.Sell,
                            Price = candidate.Price,
                            Quantity = take,
                            Fee = fee,
                            FeeAsset = candidate.CommissionAsset,
                            FeeQuote = feeQuote,
                            Maker = candidate.Maker,
                            FilledAt = candidate.Time,
                            CreatedAt = now,
                        };
                }

                if (need > 0m)
                {
                    logger.LogWarning(
                        "{Quantity} of trade {TradeId} was closed outside Ecnanib, and the account's trades don't show at what price",
                        need,
                        trade.Id);
                }
            }
        }

        return [.. fills.Values];
    }

    private static FillRow Merge(FillRow fill, decimal quantity, decimal fee, decimal? feeQuote) =>
        new()
        {
            Id = fill.Id,
            TradeId = fill.TradeId,
            OrderId = fill.OrderId,
            Source = fill.Source,
            ExchangeTradeId = fill.ExchangeTradeId,
            ExchangeOrderId = fill.ExchangeOrderId,
            Side = fill.Side,
            Price = fill.Price,
            Quantity = fill.Quantity + quantity,
            Fee = fill.Fee + fee,
            FeeAsset = fill.FeeAsset,
            FeeQuote = fill.FeeQuote is { } before && feeQuote is { } more ? before + more : null,
            Maker = fill.Maker,
            FilledAt = fill.FilledAt,
            CreatedAt = fill.CreatedAt,
        };

    /// <summary>
    /// Binance's IDs for the agent's own orders in the pair, which aren't the user's: each order's, and each Futures stop
    /// or target's the order it became, read now for one from before the journal that finished since <paramref name="since"/>.
    /// </summary>
    private async Task<HashSet<long>> AgentOrderIdsAsync(
        AgentSession session, TradeRow trade, DateTimeOffset since, Pass pass, CancellationToken cancellationToken)
    {
        List<OrderRow> orders;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            orders = await context.Set<OrderRow>()
                .AsNoTracking()
                .Where(row => row.UserId == trade.UserId && row.AgentId == trade.AgentId && row.Market == trade.Market
                    && row.Environment == trade.Environment && row.Symbol == trade.Symbol && row.ExchangeOrderId != null)
                .ToListAsync(cancellationToken);
        }

        var ids = new HashSet<long>();

        foreach (var order in orders)
        {
            ids.Add(order.ExchangeOrderId!.Value);

            if (order.TriggeredOrderId is { } triggered)
            {
                ids.Add(triggered);
            }
            else if (order is { Market: Market.Futures, Type: OrderKind.StopMarket or OrderKind.TakeProfitMarket }
                && order.Status is OrderStatus.Finished or OrderStatus.Filled or OrderStatus.Triggered
                && (order.FinishedAt ?? order.UpdatedAt) >= since
                && await FillsOrderIdAsync(session, order, pass, cancellationToken) is { } read)
            {
                ids.Add(read);
            }
        }

        return ids;
    }

    /// <summary>How much of each of Binance's trades the agent's other trades have already taken as closed outside.</summary>
    private async Task<Dictionary<long, decimal>> UsedAsync(TradeRow trade, List<long> exchangeTradeIds, CancellationToken cancellationToken)
    {
        if (exchangeTradeIds.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var used = await context.Set<FillRow>()
            .AsNoTracking()
            .Where(fill => fill.Source == FillSource.Outside && fill.TradeId != trade.Id && exchangeTradeIds.Contains(fill.ExchangeTradeId)
                && context.Set<TradeRow>().Any(other => other.Id == fill.TradeId && other.AgentId == trade.AgentId && other.Symbol == trade.Symbol))
            .GroupBy(fill => fill.ExchangeTradeId)
            .Select(group => new { ExchangeTradeId = group.Key, Quantity = group.Sum(fill => fill.Quantity) })
            .ToListAsync(cancellationToken);

        return used.ToDictionary(row => row.ExchangeTradeId, row => row.Quantity);
    }

    /// <summary>
    /// The trade's share of each Futures funding payment between <paramref name="from"/> and <paramref name="until"/>:
    /// by what its parts held at the time, among everything the agent's trades in the pair held then. A payment in
    /// another asset than the quote is logged and left out.
    /// </summary>
    /// <param name="withRates">Read each payment's rate too, which is for the record, so an open trade's reads leave it to settling.</param>
    private async Task<List<FundingPaymentRow>> FundingAsync(
        AgentSession session,
        TradeRow trade,
        List<ProtectionRow> leaves,
        DateTimeOffset from,
        DateTimeOffset until,
        bool withRates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payments = await accounts.GetFundingFeesAsync(session.Key, trade.Symbol, from, until, cancellationToken);

        if (payments.Count == 0)
        {
            return [];
        }

        IReadOnlyList<ExchangeFundingRate> rates = [];

        try
        {
            if (withRates)
            {
                rates = await marketData.GetFundingRatesAsync(trade.Environment, trade.Symbol, from - RateMatch, until + RateMatch, cancellationToken);
            }
        }
        catch (ExchangeException exception)
        {
            // The rate is for the record. The amounts are what count.
            logger.LogInformation("Binance couldn't give {Symbol}'s funding rates, so trade {TradeId}'s are left out: {Failure}", trade.Symbol, trade.Id, exception.Failure);
        }

        var agentLeaves = await AgentLeavesAsync(trade, until, cancellationToken);
        var shares = new List<FundingPaymentRow>();

        foreach (var payment in payments)
        {
            if (payment.Asset != trade.QuoteAsset)
            {
                logger.LogWarning(
                    "A funding payment of {Amount} {Asset} isn't in {QuoteAsset}, so trade {TradeId} leaves it out", payment.Amount, payment.Asset, trade.QuoteAsset, trade.Id);
                continue;
            }

            var held = TradeMath.HeldAt(leaves, payment.Time);

            if (held <= 0m)
            {
                continue;
            }

            var share = TradeMath.FundingShare(payment.Amount, held, Math.Max(held, TradeMath.HeldAt(agentLeaves, payment.Time)));
            var rate = rates.Where(rate => (rate.Time - payment.Time).Duration() <= RateMatch).OrderBy(rate => (rate.Time - payment.Time).Duration()).FirstOrDefault();

            shares.Add(new FundingPaymentRow
            {
                Id = Guid.NewGuid(),
                TradeId = trade.Id,
                ExchangeTransactionId = payment.TransactionId,
                PaidAt = payment.Time,
                Rate = rate?.Rate,
                Amount = share,
                PaymentAmount = payment.Amount,
                Asset = payment.Asset,
                CreatedAt = now,
            });
        }

        return shares;
    }

    /// <summary>The leaves of every entry of the agent's in the trade's pair that was open at some point before <paramref name="until"/>.</summary>
    private async Task<List<ProtectionRow>> AgentLeavesAsync(TradeRow trade, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var parts = await context.Set<ProtectionRow>()
            .AsNoTracking()
            .Where(part => part.UserId == trade.UserId && part.AgentId == trade.AgentId && part.OpenedAt <= until
                && context.Set<OrderRow>().Any(entry => entry.Id == part.EntryId && entry.Market == trade.Market
                    && entry.Environment == trade.Environment && entry.Symbol == trade.Symbol))
            .ToListAsync(cancellationToken);

        return TradeMath.Leaves(parts);
    }

    /// <summary>How far the price went each way while the trade was open, from the finest candles one request covers. Null when they couldn't be read.</summary>
    private async Task<(decimal Adverse, decimal Favorable)?> ExcursionsAsync(TradeRow trade, CancellationToken cancellationToken)
    {
        var closedAt = trade.ClosedAt!.Value;

        if (TradeMath.ExcursionCandles(trade.OpenedAt, closedAt) is not { } range)
        {
            return null;
        }

        try
        {
            var candles = await marketData.GetCandlesAsync(
                trade.Market, trade.Environment, trade.Symbol, range.Interval, range.From, closedAt, range.Limit, cancellationToken);

            if (candles.Count == 0)
            {
                return null;
            }

            return TradeMath.Excursions(trade.Side, trade.EntryPrice, trade.Quantity, candles.Min(candle => candle.Low), candles.Max(candle => candle.High));
        }
        catch (Exception exception) when (exception is ExchangeException or ArgumentException)
        {
            logger.LogInformation("Couldn't read {Symbol}'s candles, so trade {TradeId}'s excursions are left out: {Message}", trade.Symbol, trade.Id, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Settling failed. Until <see cref="Grace"/> after the trade closed, that may pass, so it's tried again. After, the
    /// trade is settled from its order rows, with its fees and funding unknown. True when it was settled.
    /// </summary>
    private async Task<bool> FailedAsync(TradeRow trade, DateTimeOffset now, string reason)
    {
        if (now - trade.ClosedAt < Grace)
        {
            await PostponeAsync(trade, now, reason);
            return false;
        }

        using var limit = new CancellationTokenSource(OrderStore.WriteLimit);
        await using var context = await contextFactory.CreateDbContextAsync(limit.Token);

        var settled = await context.Set<TradeRow>()
            .Where(row => row.Id == trade.Id && row.SettledAt == null)
            .ExecuteUpdateAsync(
                row => row.SetProperty(column => column.SettledAt, now).SetProperty(column => column.SettleAfter, (DateTimeOffset?)null).SetProperty(column => column.UpdatedAt, now),
                limit.Token);

        if (settled > 0)
        {
            observers.Changed(trade.UserId, trade.AgentId);
        }

        logger.LogWarning(
            "Trade {TradeId} is settled from its orders alone, with its fees and funding unknown, because for a day {Reason}",
            trade.Id,
            reason);

        return true;
    }

    private async Task PostponeAsync(TradeRow trade, DateTimeOffset now, string reason)
    {
        var attempts = trade.SettleAttempts + 1;
        var after = now + TradeMath.Backoff(attempts);

        using var limit = new CancellationTokenSource(OrderStore.WriteLimit);
        await using var context = await contextFactory.CreateDbContextAsync(limit.Token);

        await context.Set<TradeRow>()
            .Where(row => row.Id == trade.Id && row.SettledAt == null)
            .ExecuteUpdateAsync(
                row => row.SetProperty(column => column.SettleAttempts, attempts).SetProperty(column => column.SettleAfter, after).SetProperty(column => column.UpdatedAt, now),
                limit.Token);

        logger.LogWarning("Trade {TradeId} isn't settled yet, because {Reason}. It's tried again after {After:O}", trade.Id, reason, after);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    /// <summary>What one pass has read, so a trade or a fee doesn't read it twice.</summary>
    private sealed class Pass
    {
        /// <summary>A quote-asset price per asset and minute, null when there's none.</summary>
        public Dictionary<(string Asset, DateTimeOffset Minute), decimal?> Rates { get; } = [];

        /// <summary>The order a Futures stop or target became, by its row.</summary>
        public Dictionary<Guid, long?> Triggered { get; } = [];

        /// <summary>The account's trades, by window.</summary>
        public Dictionary<(DateTimeOffset From, DateTimeOffset To), IReadOnlyList<ExchangeTrade>> Outside { get; } = [];
    }
}
