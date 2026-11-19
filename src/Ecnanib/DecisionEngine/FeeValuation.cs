using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Says what the fee of an execution is worth in the asset that the symbol of its order is priced
/// in, so that the fee is in every profit and loss figure whatever asset the exchange took it in
/// (FR-BIN-10). See <see cref="FeeValues"/> and "The value of a fee" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fee is valued once, as its execution is stored,</b> and the value is kept with the
/// execution: a figure that is worked out later counts the fee by what it was worth then, and
/// not by a price of its own moment.
/// </para>
/// <para>
/// <b>A fee in an asset of neither side of the symbol is priced by a symbol of the configuration
/// file:</b> the asset of the fee, priced in the asset that the traded symbol is priced in, where
/// <c>Binance:FeeSymbols</c> lists it. It is the price that the symbol last traded at on the Spot
/// market, which the gateway reads once for everyone (FR-BIN-3). A fee in an asset that the file
/// names no symbol for has no value, and is said once, as a warning.
/// </para>
/// <para>
/// <b>No price is read between an entry and its stop-loss.</b> The executions of an entry of the
/// Spot market are stored as the position is opened, which is before it is protected: a fee of
/// theirs that needs the price of another symbol is stored without its value, and is valued by
/// the next run of the job that settles the orders, within a minute.
/// </para>
/// <para>
/// <b>What could not be read is read again,</b> by that job, for as long as
/// <see cref="StoredOrders.FeesValuedWithin"/>: the rules of the symbol, which say its two
/// assets, or the price of the symbol that prices the fee.
/// </para>
/// <para>
/// It only reads the market, and is handed no key: the prices are those of everyone.
/// </para>
/// </remarks>
internal sealed class FeeValuation(
    IExchangeMarketData market, StoredOrders stored, BinanceSettings binance, ILogger<FeeValuation> logger)
{
    /// <summary>What the fee of an execution of an order is worth, or null when that is not known now.</summary>
    /// <param name="order">The order that was executed: its market, its mode, and its symbol say where the fee is valued.</param>
    /// <param name="fee">The fee.</param>
    /// <param name="feeAsset">The asset that the exchange took it in.</param>
    /// <param name="price">The price of the execution.</param>
    /// <param name="readsPrice">
    /// Whether the price of another symbol is read for it now, where it needs one. It is not
    /// while a position waits for its stop-loss: such a fee is valued a moment later.
    /// </param>
    /// <param name="again">Whether it is asked once more, for an execution that is stored already: what was said the first time is not said again.</param>
    /// <param name="cancellationToken">Ends the wait for a read.</param>
    public async Task<decimal?> OfAsync(
        Order order, decimal fee, string feeAsset, decimal price, bool readsPrice, bool again, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        // No fee is nothing, in whatever asset the exchange names beside it.
        if (fee == 0m)
        {
            return 0m;
        }

        // The assets of the symbol, which the gateway reads once for everyone (FR-BIN-3).
        var listing = await market.GetSymbolsAsync(order.Market, order.Mode, cancellationToken);

        if (listing.Outcome != ExchangeCallOutcome.Succeeded
            || listing.Symbols.FirstOrDefault(listed => listed.Symbol == order.Symbol) is not { } symbol)
        {
            logger.Log(
                again ? LogLevel.Debug : LogLevel.Warning,
                "A fee of agent {AgentId} in {Symbol} is not valued yet: the exchange did not say the assets of the symbol ({ExchangeCallOutcome})",
                order.AgentId,
                order.Symbol,
                listing.Outcome);

            return null;
        }

        decimal? worth = null;

        if (FeeValues.PricedBy(feeAsset, symbol.BaseAsset, symbol.QuoteAsset) is { } pricedBy)
        {
            if (!binance.FeeSymbols.Contains(pricedBy))
            {
                logger.Log(
                    again ? LogLevel.Debug : LogLevel.Warning,
                    "A fee of agent {AgentId} in {Symbol} was taken in {FeeAsset}, which the configuration file names no symbol for: it has no value, and is in no profit and loss figure",
                    order.AgentId,
                    order.Symbol,
                    feeAsset);

                return null;
            }

            if (!readsPrice)
            {
                // Valued by the next run of what settles the orders: nothing waits for a price here.
                return null;
            }

            var read = await market.GetPriceAsync(Market.Spot, order.Mode, pricedBy, cancellationToken);

            if (read is not { Outcome: ExchangeCallOutcome.Succeeded, Price: { } traded })
            {
                logger.Log(
                    again ? LogLevel.Debug : LogLevel.Warning,
                    "A fee of agent {AgentId} in {Symbol} is not valued yet: the exchange did not give the price of {FeeSymbol} ({ExchangeCallOutcome})",
                    order.AgentId,
                    order.Symbol,
                    pricedBy,
                    read.Outcome);

                return null;
            }

            worth = traded;
        }

        return FeeValues.Of(fee, feeAsset, price, symbol.BaseAsset, symbol.QuoteAsset, worth);
    }

    /// <summary>
    /// Values the fees of the executions that are stored without a value, as far as they are
    /// still valued: what could not be read when they were stored is read again.
    /// </summary>
    /// <returns>How many were valued.</returns>
    public async Task<int> ValueStoredAsync(CancellationToken cancellationToken)
    {
        var valued = 0;

        foreach (var (fill, order) in await stored.UnvaluedFeesAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await OfAsync(order, fill.Fee, fill.FeeAsset, fill.Price, readsPrice: true, again: true, cancellationToken) is { } value
                && await stored.ValueFeeAsync(fill.Id, value, CancellationToken.None))
            {
                valued++;
            }
        }

        if (valued > 0)
        {
            logger.LogInformation("The fees of {Fills} executions that were stored without a value were valued", valued);
        }

        return valued;
    }
}
