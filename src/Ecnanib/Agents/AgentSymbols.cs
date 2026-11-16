using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.Agents;

/// <summary>
/// The symbols that an agent can be given, as the exchange says them: those of the configuration
/// file that a market has in an environment (FR-AGT-6). What keeps the settings of agents asks
/// here, and so does what switches the mode of one: a symbol is held to the market of the
/// environment that the agent trades in. See docs/agents.md.
/// </summary>
internal static class AgentSymbols
{
    /// <summary>
    /// The symbols of each market in an environment. The answer of the exchange is shared and
    /// kept for a while, so this is seldom a call.
    /// </summary>
    public static async Task<IReadOnlyList<MarketSymbols>> ReadAsync(
        IExchangeMarketData marketData, BinanceSettings binance, TradingMode mode, CancellationToken cancellationToken)
    {
        var markets = Enum.GetValues<Market>();
        var read = await Task.WhenAll(markets.Select(market => marketData.GetSymbolsAsync(market, mode, cancellationToken)));

        return
        [
            .. markets.Zip(read, (market, symbols) => new MarketSymbols(
                market,
                symbols.Outcome == ExchangeCallOutcome.Succeeded,
                [
                    // Of the file alone, whatever else the market has: an agent is given no other (FR-AGT-6).
                    .. symbols.Symbols
                        .Where(symbol => binance.Symbols.Contains(symbol.Symbol, StringComparer.Ordinal))
                        .Select(symbol => new SymbolChoice(symbol.Symbol, symbol.QuoteAsset)),
                ])),
        ];
    }

    /// <summary>What a symbol is priced in on a market, among the symbols that were read.</summary>
    /// <returns>
    /// Whether the exchange said which symbols the market has, and the quote asset of the
    /// symbol, which is null when the market does not have it.
    /// </returns>
    public static (bool Known, string? QuoteAsset) QuoteAssetOf(
        this IReadOnlyList<MarketSymbols> symbols, Market market, string symbol)
    {
        var there = symbols.Single(read => read.Market == market);

        return there.Known
            ? (true, there.Symbols.FirstOrDefault(listed => string.Equals(listed.Symbol, symbol, StringComparison.Ordinal))?.QuoteAsset)
            : (false, null);
    }
}
