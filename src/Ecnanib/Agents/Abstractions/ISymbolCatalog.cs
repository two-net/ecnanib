using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The tradable symbols of a market with their trading filters, tick size, lot size and minimum notional
/// (FR-DATA-01), for the creation check and for order rounding (FR-EXE-04). Seeded by the agents issue with a read of
/// the exchange per call; the market-data module keeps a listing per environment and market and refreshes it at
/// least daily. Register an implementation before <c>AddAgents</c> to replace the default. When nothing is known yet
/// and the exchange cannot be reached, the exchange's exception propagates so a caller can tell "not listed" from
/// "could not check".
/// </summary>
public interface ISymbolCatalog
{
    /// <summary>The symbol as the exchange lists it, or null when the market does not list it; the comparison ignores case.</summary>
    Task<SymbolInfo?> FindAsync(ExchangeEnvironment environment, MarketType market, string symbol, CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolInfo>> ListAsync(ExchangeEnvironment environment, MarketType market, CancellationToken cancellationToken);
}
