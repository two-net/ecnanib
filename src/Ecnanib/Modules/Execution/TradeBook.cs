using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Books an agent's trades from its <c>orders</c> and <c>protections</c> alone, with no exchange call, so the loss
/// limits see a trade the moment its last part ends (FR-EXE-07, FR-RISK-08). It opens a trade when an entry's first
/// part is protected, keeps its quantity and cycles up to date while it's open, and closes it once the entry has ended
/// and every part has, with provisional figures from the order rows. Settling replaces them with Binance's (see
/// <see cref="TradeSettlement"/>). Its caller holds the agent's lock, so bookings never overlap. See The trade journal in
/// docs/architecture.md.
/// </summary>
internal sealed class TradeBook(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IExchangeSymbols symbols,
    TimeProvider time,
    ILogger<TradeBook> logger)
{
    /// <summary>
    /// Books what has changed. A failure is logged, never thrown: the protective work it follows must go on, and the
    /// journal's sweep books it again.
    /// </summary>
    /// <param name="pair">The agent's pair as the caller already knows it, which saves looking it up.</param>
    public async Task TryBookAsync(Guid userId, Guid agentId, ExchangeSymbolInfo? pair, CancellationToken cancellationToken)
    {
        try
        {
            await BookAsync(userId, agentId, pair, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Booking the agent's trades failed, so the journal's sweep tries again");
        }
    }

    public async Task BookAsync(Guid userId, Guid agentId, ExchangeSymbolInfo? pair, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Entries with a protected part whose trade is missing or still open.
        var entries = await context.Set<OrderRow>()
            .AsNoTracking()
            .Where(entry => entry.UserId == userId && entry.AgentId == agentId && entry.Leg == OrderLeg.Entry
                && context.Set<ProtectionRow>().Any(part => part.EntryId == entry.Id)
                && !context.Set<TradeRow>().Any(trade => trade.EntryId == entry.Id && trade.Status == TradeStatus.Closed))
            .OrderBy(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return;
        }

        var entryIds = entries.Select(entry => entry.Id).ToList();

        var parts = await context.Set<ProtectionRow>()
            .AsNoTracking()
            .Where(part => part.UserId == userId && part.AgentId == agentId && entryIds.Contains(part.EntryId))
            .ToListAsync(cancellationToken);

        var legs = await context.Set<OrderRow>()
            .AsNoTracking()
            .Where(leg => leg.UserId == userId && leg.AgentId == agentId && leg.EntryId != null && entryIds.Contains(leg.EntryId.Value))
            .ToListAsync(cancellationToken);

        var trades = await context.Set<TradeRow>()
            .Where(trade => entryIds.Contains(trade.EntryId))
            .ToDictionaryAsync(trade => trade.EntryId, cancellationToken);

        var tradeIds = trades.Values.Select(trade => trade.Id).ToList();

        var linked = (await context.Set<TradeCycleRow>()
                .AsNoTracking()
                .Where(link => tradeIds.Contains(link.TradeId))
                .Select(link => new { link.TradeId, link.CycleId })
                .ToListAsync(cancellationToken))
            .Select(link => (link.TradeId, link.CycleId))
            .ToHashSet();

        var now = time.GetUtcNow();
        var changed = false;

        foreach (var entry in entries)
        {
            var entryParts = parts.Where(part => part.EntryId == entry.Id).ToList();
            var entryLegs = legs.Where(leg => leg.EntryId == entry.Id).ToList();

            if (!trades.TryGetValue(entry.Id, out var trade))
            {
                var assets = await AssetsAsync(entry, pair, cancellationToken);

                if (assets is null)
                {
                    logger.LogWarning(
                        "Entry {ClientOrderId}'s trade isn't booked yet, because its pair {Symbol} couldn't be found", entry.ClientOrderId, entry.Symbol);
                    continue;
                }

                trade = Open(entry, entryParts, assets.Value, now);
                context.Add(trade);
                trades[entry.Id] = trade;
                changed = true;

                logger.LogInformation("Entry {ClientOrderId} opened trade {TradeId}", entry.ClientOrderId, trade.Id);
            }

            changed |= Update(trade, entry, entryParts, now);

            // The cycles whose orders made or moved it (FR-EXE-07).
            foreach (var cycleId in entryLegs.Select(leg => leg.CycleId).Concat(entryParts.Select(part => part.CycleId)).Prepend(entry.CycleId).Distinct())
            {
                if (linked.Add((trade.Id, cycleId)))
                {
                    context.Add(new TradeCycleRow { TradeId = trade.Id, CycleId = cycleId });
                    changed = true;
                }
            }

            if (Closable(entry, entryParts))
            {
                Close(trade, entryParts, entryLegs, now);
                changed = true;

                logger.LogInformation(
                    "Trade {TradeId} of entry {ClientOrderId} closed: {ExitReason}, {RealizedPnl} {QuoteAsset} before fees and funding",
                    trade.Id,
                    entry.ClientOrderId,
                    trade.ExitReason,
                    trade.RealizedPnl,
                    trade.QuoteAsset);
            }
        }

        if (changed)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The entry has ended, with every part that could be protected protected, and every part has ended.</summary>
    public static bool Closable(OrderRow entry, IReadOnlyCollection<ProtectionRow> parts)
    {
        var leaves = TradeMath.Leaves(parts);

        return entry.Resolved && leaves.Count > 0 && leaves.All(TradeMath.IsTerminal);
    }

    private static TradeRow Open(OrderRow entry, List<ProtectionRow> parts, (string Base, string Quote) assets, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = entry.UserId,
            AgentId = entry.AgentId,
            EntryId = entry.Id,
            CredentialId = entry.CredentialId,
            Market = entry.Market,
            Environment = entry.Environment,
            Symbol = entry.Symbol,
            BaseAsset = assets.Base,
            QuoteAsset = assets.Quote,
            Side = ProtectionKeeper.SideOf(entry),
            Status = TradeStatus.Open,
            OpenedAt = parts.Min(part => part.OpenedAt),
            InitialStop = entry.ProtectiveStop ?? parts[0].StopPrice,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>What an open trade holds so far: a limit entry's later fills add parts.</summary>
    private static bool Update(TradeRow trade, OrderRow entry, List<ProtectionRow> parts, DateTimeOffset now)
    {
        if (trade.Status != TradeStatus.Open)
        {
            return false;
        }

        var quantity = TradeMath.Leaves(parts).Sum(part => part.Quantity);
        var entryPrice = entry.AveragePrice ?? parts.Min(part => part.EntryPrice);
        var openedAt = parts.Min(part => part.OpenedAt);

        if (trade.Quantity == quantity && trade.EntryPrice == entryPrice && trade.OpenedAt == openedAt)
        {
            return false;
        }

        trade.Quantity = quantity;
        trade.EntryPrice = entryPrice;
        trade.OpenedAt = openedAt;
        trade.UpdatedAt = now;

        return true;
    }

    /// <summary>
    /// Closes the trade with figures from the order rows: each exit's executed quantity at its average price. A Futures
    /// stop or target that executed reports no quantity of its own, so its part's quantity at its trigger price stands
    /// in until settling reads its fills. Fees and funding wait for settling.
    /// </summary>
    private static void Close(TradeRow trade, List<ProtectionRow> parts, List<OrderRow> legs, DateTimeOffset now)
    {
        var leaves = TradeMath.Leaves(parts);
        var exits = ExitsFromRecords(trade, parts, legs);
        var exitQuantity = exits.Sum(exit => exit.Quantity);
        var exitNotional = exits.Sum(exit => exit.Price * exit.Quantity);
        var gross = TradeMath.Gross(trade.Side, trade.EntryPrice, exitNotional, exitQuantity);

        trade.Status = TradeStatus.Closed;
        trade.ClosedAt = leaves.Max(TradeMath.EndOf);
        trade.ExitReason = TradeMath.ExitReason(leaves);
        trade.ExitQuantity = exitQuantity;
        trade.ExitPrice = TradeMath.Average(exits);
        trade.UnpricedQuantity = Math.Max(0m, trade.Quantity - exitQuantity);
        trade.Fees = null;
        trade.Funding = trade.Market == Market.Spot ? 0m : null;
        trade.RealizedPnl = gross;
        trade.RMultiple = TradeMath.RMultiple(gross, trade.EntryPrice, trade.InitialStop, exitQuantity);
        trade.SettleAfter = now;
        trade.UpdatedAt = now;
    }

    /// <summary>Each exit's quantity and price as the order rows have them.</summary>
    public static List<(decimal Price, decimal Quantity)> ExitsFromRecords(TradeRow trade, List<ProtectionRow> parts, List<OrderRow> legs)
    {
        var exits = new List<(decimal Price, decimal Quantity)>();

        foreach (var leg in legs.Where(leg => leg.Leg != OrderLeg.Entry))
        {
            if (leg.ExecutedQuantity > 0m && (leg.AveragePrice ?? leg.Price ?? leg.StopPrice) is { } price)
            {
                exits.Add((price, leg.ExecutedQuantity));
            }
            else if (ExecutedWithoutQuantity(trade, leg, parts) && leg.StopPrice is { } trigger)
            {
                exits.Add((trigger, leg.Quantity));
            }
        }

        return exits;
    }

    /// <summary>
    /// A Futures stop or target that Binance shows finished, whose part ended because it executed, but whose algo order
    /// reports no executed quantity.
    /// </summary>
    public static bool ExecutedWithoutQuantity(TradeRow trade, OrderRow leg, IEnumerable<ProtectionRow> parts) =>
        trade.Market == Market.Futures
        && leg.ExecutedQuantity == 0m
        && leg.Status is OrderStatus.Finished or OrderStatus.Filled
        && (leg.Leg, parts.FirstOrDefault(part => part.Id == leg.ProtectionId)?.EndReason) is
            (OrderLeg.Stop, ProtectionEnd.StopFilled) or (OrderLeg.Target, ProtectionEnd.TargetFilled);

    private async Task<(string Base, string Quote)?> AssetsAsync(OrderRow entry, ExchangeSymbolInfo? pair, CancellationToken cancellationToken)
    {
        if (pair is not null && pair.Market == entry.Market && pair.Environment == entry.Environment && pair.Symbol == entry.Symbol)
        {
            return (pair.BaseAsset, pair.QuoteAsset);
        }

        try
        {
            return await symbols.FindAsync(entry.Market, entry.Environment, entry.Symbol, cancellationToken) is { } found
                ? (found.BaseAsset, found.QuoteAsset)
                : null;
        }
        catch (MarketDataException exception)
        {
            logger.LogWarning("Couldn't find {Symbol}'s assets: {Message}", entry.Symbol, exception.Message);
            return null;
        }
    }
}
