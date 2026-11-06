using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Reconciles an agent's orders with Binance (FR-EXE-06), then reads its balances, and on Futures the symbol's leverage
/// brackets, through its own exchange key, for one use, and disposes the key after (FR-KEY-09). The position and the
/// pending entry come from Execution's records, as reconciliation left them. See Decision cycle and Reconciliation in
/// docs/architecture.md.
/// </summary>
internal sealed class AgentAccounts(
    ICredentialKeys keys,
    IExchangeAccount accounts,
    OrderStore orders,
    AgentReconciliation reconciliation,
    ILogger<AgentAccounts> logger) : IAgentAccounts
{
    public async Task<AgentAccount> ReadAsync(
        Guid userId,
        Guid agentId,
        Guid credentialId,
        Market market,
        string symbol,
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken)
    {
        Reconciliation? reconciled = null;

        try
        {
            reconciled = await reconciliation.ReconcileAsync(userId, agentId, resume: false, skipIfBusy: false, cancellationToken);
        }
        catch (AgentSessionException exception) when (exception.Failure == AgentSessionFailure.KeyUnusable)
        {
            throw new AgentAccountException(AgentAccountFailure.KeyUnusable, exception.Message, exception);
        }
        catch (AgentSessionException exception)
        {
            logger.LogWarning("The agent's orders couldn't be reconciled, so its position is as recorded: {Failure}", exception.Failure);
        }

        if (reconciled is { Verified: false })
        {
            logger.LogWarning("Binance couldn't confirm the agent's orders, so its position is as recorded");
        }

        ExchangeKey key;

        try
        {
            key = await keys.OpenExchangeKeyAsync(userId, credentialId, market, cancellationToken);
        }
        catch (CredentialUnusableException exception)
        {
            throw new AgentAccountException(
                AgentAccountFailure.KeyUnusable, $"Exchange key {credentialId} can't be used: {exception.Reason}", exception);
        }

        ExchangeAccount account;
        IReadOnlyList<LeverageBracket> brackets = [];

        try
        {
            var accountRead = accounts.GetAccountAsync(market, key, cancellationToken);
            var bracketsRead = market == Market.Futures ? BracketsAsync(key, symbol, credentialId, cancellationToken) : null;

            // Both end before the key is disposed, whichever fails.
            await ((Task)Task.WhenAll(bracketsRead is null ? [accountRead] : [accountRead, bracketsRead]))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            account = await accountRead;
            brackets = bracketsRead is null ? [] : await bracketsRead;
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning("Reading the account behind exchange key {CredentialId} failed: {Failure}", credentialId, exception.Failure);

            throw new AgentAccountException(
                AgentAccountFailure.ExchangeUnavailable, $"Binance couldn't give the account: {exception.Failure}", exception);
        }
        finally
        {
            key.ApiKey.Dispose();
            key.Secret.Dispose();
        }

        // The entry that hasn't ended yet, which the Risk Engine won't stack another on (RiskRule.PendingEntry).
        var pending = await orders.HasPendingEntryAsync(userId, agentId, cancellationToken);
        var records = reconciled?.Orders ?? await orders.LoadAsync(userId, agentId, cancellationToken);
        var held = AgentPositions.Of(records, reconciled?.Positions);

        var result = market == Market.Spot
            ? new AgentAccount(
                Balance(account, quoteAsset)?.Available ?? 0m,
                Balance(account, baseAsset)?.Total ?? 0m,
                held?.Position,
                pending,
                Brackets: [])
            : new AgentAccount(account.AvailableBalance ?? 0m, null, held?.Position, pending, brackets);

        return result with
        {
            PositionOpenedAt = held?.OpenedAt,
            Targets = held?.Targets ?? [],
            LiquidationPrice = held?.LiquidationPrice,
            PendingEntry = AgentPositions.PendingEntry(records),
        };
    }

    /// <summary>
    /// The symbol's leverage brackets, or none when Binance couldn't give them. The Risk Engine then fails every entry
    /// for want of a liquidation price, while an exit or a tighter stop still goes through in this cycle.
    /// </summary>
    private async Task<IReadOnlyList<LeverageBracket>> BracketsAsync(
        ExchangeKey key, string symbol, Guid credentialId, CancellationToken cancellationToken)
    {
        try
        {
            var brackets = await accounts.GetLeverageBracketsAsync(key, symbol, cancellationToken);

            return
            [
                .. brackets.Select(bracket => new LeverageBracket(
                    bracket.NotionalFloor,
                    bracket.NotionalCap,
                    bracket.MaintenanceMarginRate,
                    bracket.MaintenanceAmount,
                    bracket.InitialLeverage)),
            ];
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning(
                "Binance couldn't give {Symbol}'s leverage brackets through exchange key {CredentialId}, so no entry can pass: {Failure}",
                symbol,
                credentialId,
                exception.Failure);

            return [];
        }
    }

    /// <summary>Binance leaves out assets with nothing in them, so a missing one holds nothing.</summary>
    private static AssetBalance? Balance(ExchangeAccount account, string asset) =>
        account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, asset, StringComparison.Ordinal));
}

/// <summary>
/// The agent's position as its records hold it: the parts its protections hold. See Reconciliation in
/// docs/architecture.md.
/// </summary>
internal static class AgentPositions
{
    /// <summary>An average of parts bought at different prices is kept to this many places, like Binance's own prices.</summary>
    public const int AveragePlaces = 8;

    /// <summary>The held parts as one position, or null when nothing is held.</summary>
    /// <param name="positions">Futures: Binance's positions in the symbol, for the break-even and liquidation prices.</param>
    public static HeldPosition? Of(AgentOrders orders, IReadOnlyList<FuturesPosition>? positions)
    {
        var held = orders.Protections.Where(protection => OrderStore.HeldProtections.Contains(protection.Status)).ToList();

        if (held.Count == 0)
        {
            return null;
        }

        var side = ProtectionKeeper.SideOf(orders.EntryOf(held[0]));
        var quantity = held.Sum(protection => protection.Quantity);
        var average = Math.Round(held.Sum(protection => protection.Quantity * protection.EntryPrice) / quantity, AveragePlaces);
        var stop = side == PositionSide.Long ? held.Max(protection => protection.StopPrice) : held.Min(protection => protection.StopPrice);
        var targets = held
            .Where(protection => !protection.TargetDropped && protection.TargetPrice is not null)
            .Select(protection => protection.TargetPrice!.Value)
            .Distinct()
            .ToList();

        IReadOnlyList<decimal> nearest = side == PositionSide.Long ? [.. targets.Order()] : [.. targets.OrderDescending()];

        // Binance's figures are for its whole position in the symbol, which are the agent's only when the two agree.
        var own = positions?.Where(position => position.Amount > 0m == (side == PositionSide.Long)).ToList();
        var binance = own is [var single] && Math.Abs(single.Amount) == quantity ? single : null;

        return new HeldPosition(
            new OpenPosition(side, quantity, average, binance?.BreakEvenPrice is > 0m ? binance.BreakEvenPrice : null, stop, nearest.Count > 0 ? nearest[0] : null),
            held.Min(protection => protection.OpenedAt),
            nearest,
            binance?.LiquidationPrice);
    }

    /// <summary>The newest entry that hasn't ended, or null.</summary>
    public static AgentPendingEntry? PendingEntry(AgentOrders orders) =>
        orders.Entries.LastOrDefault(entry => OrderStore.PendingEntryStatuses.Contains(entry.Status)) is { } entry
            ? new AgentPendingEntry(
                ProtectionKeeper.SideOf(entry),
                entry.Type == OrderKind.Limit ? EntryType.Limit : EntryType.Market,
                entry.Type == OrderKind.Limit ? entry.Price : null,
                entry.Quantity,
                entry.ExecutedQuantity,
                entry.SentAt ?? entry.CreatedAt)
            : null;

    /// <summary>A position whose parts the agent holds.</summary>
    /// <param name="Targets">Its distinct take-profits, nearest first.</param>
    /// <param name="LiquidationPrice">Futures: Binance's, when Binance's position is the agent's own.</param>
    internal sealed record HeldPosition(OpenPosition Position, DateTimeOffset OpenedAt, IReadOnlyList<decimal> Targets, decimal? LiquidationPrice);
}
