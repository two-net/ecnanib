using System.Text.Json;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>Higher-timeframe bias (FR-STR-02).</summary>
public enum BiasDirection
{
    None = 0,
    Long = 1,
    Short = 2,
}

public sealed record PriceLevel(string Name, decimal Price);

/// <summary>
/// A candidate setup (FR-STR-02): proposed entry, initial stop, targets, invalidation and the filters applied.
/// <see cref="SizeMultiplier"/> is the Appendix A weekend size multiplier (1 when none applies), for execution to apply.
/// </summary>
public sealed record StrategySetup(
    PositionSide Side,
    OrderType EntryType,
    decimal Entry,
    decimal Stop,
    IReadOnlyList<decimal> Targets,
    string Invalidation,
    IReadOnlyList<string> Filters,
    decimal SizeMultiplier = 1m);

/// <summary>The engine's output for one evaluation (FR-STR-02), stamped with the strategy and version that produced it; stored on the evaluation as JSON.</summary>
public sealed record StrategyOutput(
    string StrategyId,
    int StrategyVersion,
    BiasDirection Bias,
    IReadOnlyList<PriceLevel> Levels,
    IReadOnlyDictionary<string, decimal> Indicators,
    StrategySetup? Setup,
    string? Note)
{
    public static StrategyOutput Nothing(StrategyDescriptor strategy, string? note) =>
        new(strategy.Id, strategy.Version, BiasDirection.None, [], new Dictionary<string, decimal>(), null, note);
}

/// <summary>
/// What the engine evaluates: the agent (with <see cref="Agent.EntryWindows"/> loaded, FR-STR-05), the descriptor of
/// its pinned strategy version, the resolved parameters, the market snapshot and whether new entries are allowed.
/// <see cref="EntriesAllowed"/> is false for a Paused agent (FR-AGT-05): the engine still reports bias, levels and
/// indicators for position management. Time-based rules use the evaluated candle's close, never the wall clock (FR-STR-01).
/// </summary>
public sealed record StrategyContext(
    Agent Agent,
    StrategyDescriptor Strategy,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    MarketSnapshot Market,
    bool EntriesAllowed);

/// <summary>
/// The deterministic strategy engine (FR-STR-01): same candles and parameters, same output. Provided by the Strategies
/// module (<c>AddStrategies</c>); an implementation registered before it wins.
/// </summary>
public interface IStrategyEngine
{
    StrategyOutput Evaluate(StrategyContext context);
}
