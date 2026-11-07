using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// What the cycle gives the council and the Risk Engine, built from what it collected. Each builder reads nothing but
/// its arguments. See the Risk Engine's Inputs in docs/architecture.md.
/// </summary>
internal static class CycleInputs
{
    /// <summary>
    /// The Strategist's proposal, with the Risk Critic's changes applied, as an intent the engine checks again
    /// (NFR-SEC-08). The contract has already refused a change that adds risk.
    /// </summary>
    /// <param name="positionSide">The agent's position's side, for an action on it. Null when it has none.</param>
    /// <exception cref="ArgumentException">The proposal holds.</exception>
    public static OrderIntent Intent(TradeProposal proposal, VerdictModifications? changes, PositionSide? positionSide)
    {
        var action = ActionOf(proposal.Action)
            ?? throw new ArgumentException("A HOLD has no intent.", nameof(proposal));
        var entry = TradeActions.IsEntry(proposal.Action);
        var targets = changes is { TargetPrices.Count: > 0 } ? changes.TargetPrices : proposal.TargetPrices;
        var side = entry ? proposal.Side : positionSide;

        return new OrderIntent
        {
            Action = action,
            EntryType = entry ? proposal.EntryType : null,
            LimitPrice = entry && proposal.EntryType == EntryType.Limit ? proposal.EntryPrice : null,
            StopPrice = changes?.StopPrice ?? proposal.StopPrice,
            TargetPrice = Nearest(targets, side),
            RiskPercent = entry ? changes?.RiskPercent ?? proposal.RiskPercent : null,
            CloseFraction = TradeActions.IsExit(proposal.Action) ? proposal.CloseFraction : null,
        };
    }

    /// <summary>FR-CYC-03's actions as the engine names them. HOLD has none, because nothing is sent.</summary>
    public static IntentAction? ActionOf(TradeAction action) => action switch
    {
        TradeAction.Buy => IntentAction.Buy,
        TradeAction.Sell => IntentAction.Sell,
        TradeAction.OpenLong => IntentAction.OpenLong,
        TradeAction.OpenShort => IntentAction.OpenShort,
        TradeAction.Close => IntentAction.Close,
        TradeAction.AdjustStop => IntentAction.AdjustStop,
        TradeAction.AdjustTarget => IntentAction.AdjustTarget,
        TradeAction.CancelPending => IntentAction.CancelPending,
        TradeAction.Hold => null,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not an action."),
    };

    /// <summary>
    /// The target closest to the entry, which gives the lowest reward-to-risk, so the engine checks the worst case:
    /// the lowest for a long and the highest for a short. With no side to judge by, the first.
    /// </summary>
    public static decimal? Nearest(IReadOnlyList<decimal> targets, PositionSide? side) => targets.Count == 0
        ? null
        : side switch
        {
            PositionSide.Long => targets.Min(),
            PositionSide.Short => targets.Max(),
            _ => targets[0],
        };

    public static RiskInputs Risk(
        Market market,
        DateTimeOffset now,
        RiskSettings settings,
        decimal capitalAllocation,
        ExchangeSymbolInfo symbol,
        MarketSnapshot snapshot,
        AgentAccount account,
        TradingRecord record) => new()
    {
        Market = market,
        Now = now,
        Settings = settings,
        CapitalAllocation = capitalAllocation,
        Filters = new ExchangeFilters(
            symbol.IsTrading,
            symbol.Price is { } price ? new TickRule(price.Minimum, price.Maximum, price.TickSize) : null,
            Lot(symbol.Quantity),
            Lot(symbol.MarketQuantity),
            symbol.Notional is { } notional ? new NotionalRule(notional.Minimum, notional.Maximum) : null,
            symbol.PercentPrice is { } band ? new PercentPriceRule(band.BuyUp, band.BuyDown, band.SellUp, band.SellDown) : null),
        Prices = new MarketPrices(snapshot.Quote.BidPrice, snapshot.Quote.AskPrice, snapshot.Quote.LastPrice, snapshot.Futures?.MarkPrice),
        Atr = snapshot.Candles.Indicators.Atr14,

        Funding = snapshot.Futures is { FundingRate: { } rate } futures ? new FundingInfo(rate, futures.FundingIntervalHours) : null,
        AvailableQuote = account.AvailableQuote,
        BaseHeld = account.BaseHeld,
        Position = account.Position,
        HasPendingEntry = account.HasPendingEntry,

        Record = record,
        Brackets = account.Brackets,
    };

    /// <summary>The account as the Strategist and Risk Critic see it, with the leverage the caps allow on Futures.</summary>
    public static PromptAccount Account(
        Market market, ExchangeSymbolInfo symbol, decimal capitalAllocation, AgentAccount account, RiskSettings effective) =>
        new(symbol.BaseAsset, symbol.QuoteAsset, capitalAllocation, account.AvailableQuote)
        {
            BaseHeld = market == Market.Spot ? account.BaseHeld : null,
            Leverage = market == Market.Futures ? effective.MaxLeverage : null,
        };

    /// <summary>
    /// The agent's position as the Strategist and Risk Critic see it, with what it would make or lose closed now: at the
    /// mark price on Futures, and at the best price it could sell or buy at on Spot.
    /// </summary>
    public static PromptPosition? Position(Market market, MarketSnapshot snapshot, AgentAccount account)
    {
        if (account is not { Position: { } position, PositionOpenedAt: { } openedAt })
        {
            return null;
        }

        var price = market == Market.Futures
            ? snapshot.Futures?.MarkPrice
            : position.Side == PositionSide.Long ? snapshot.Quote.BidPrice : snapshot.Quote.AskPrice;
        var sign = position.Side == PositionSide.Long ? 1m : -1m;

        return new PromptPosition(position.Side, position.Quantity, position.AverageEntry, openedAt)
        {
            Stop = position.Stop,
            Targets = account.Targets,
            UnrealizedPnl = price is { } at && at > 0m ? Math.Round(sign * (at - position.AverageEntry) * position.Quantity, 8) : null,
            LiquidationPrice = market == Market.Futures ? account.LiquidationPrice : null,
        };
    }

    /// <summary>The agent's entry that hasn't filled, as the Strategist and Risk Critic see it.</summary>
    public static PromptPendingEntry? PendingEntry(AgentAccount account) =>
        account.PendingEntry is { } entry ? new PromptPendingEntry(entry.Side, entry.Type, entry.Price, entry.Quantity, entry.PlacedAt) : null;

    public static InputSnapshot Snapshot(
        MarketSnapshot snapshot, AgentAccount account, decimal capitalAllocation, RiskSettings effective) => new(
        snapshot.Symbol,
        snapshot.Candles.LastOpenTime + snapshot.Candles.Interval,
        snapshot.Candles.Candles.Count > 0 ? snapshot.Candles.Candles[^1] : null,
        snapshot.HigherTimeframe.Candles.Count > 0 ? snapshot.HigherTimeframe.Candles[^1] : null,
        snapshot.Candles.Indicators,
        snapshot.HigherTimeframe.Indicators,
        snapshot.Quote,
        snapshot.Futures,
        account.AvailableQuote,
        account.BaseHeld,
        account.Position,
        account.HasPendingEntry,
        capitalAllocation,
        effective);

    private static LotRule? Lot(QuantityFilter? filter) =>
        filter is null ? null : new LotRule(filter.Minimum, filter.Maximum, filter.StepSize);
}
