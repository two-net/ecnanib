using Ecnanib.Agents.Abstractions;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>
/// A strategy engine for the live pipeline tests that reports no setup but an ATR: the real engine finds the three
/// candles the fakes serve insufficient and reports no indicators, and the fail-safe stop-distance check (NFR-REL-03)
/// would then refuse every discretionary entry the council proposes.
/// </summary>
internal sealed class AtrOnlyEngine : IStrategyEngine
{
    public StrategyOutput Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var close = context.Market.Latest?.Close ?? 100m;
        return new StrategyOutput(context.Strategy.Id, context.Strategy.Version, BiasDirection.Long, [new PriceLevel("emaFast", close - 1m)],
            new Dictionary<string, decimal> { ["atr"] = 2m, ["close"] = close }, null, "no setup (fake engine with an ATR)");
    }
}
