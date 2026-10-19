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

/// <summary>A candidate setup (FR-STR-02): proposed entry, initial stop, targets, invalidation and the filters that passed.</summary>
public sealed record StrategySetup(
    PositionSide Side,
    OrderType EntryType,
    decimal Entry,
    decimal Stop,
    IReadOnlyList<decimal> Targets,
    string Invalidation,
    IReadOnlyList<string> Filters);

/// <summary>The engine's output for one evaluation (FR-STR-02); stored on the evaluation as JSON.</summary>
public sealed record StrategyOutput(
    BiasDirection Bias,
    IReadOnlyList<PriceLevel> Levels,
    IReadOnlyDictionary<string, decimal> Indicators,
    StrategySetup? Setup,
    string? Note)
{
    public static StrategyOutput Nothing(string? note) => new(BiasDirection.None, [], new Dictionary<string, decimal>(), null, note);
}

/// <summary><see cref="EntriesAllowed"/> is false for a Paused agent (FR-AGT-05): the engine still reports levels for position management.</summary>
public sealed record StrategyContext(
    Agent Agent,
    StrategyDescriptor Strategy,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    MarketSnapshot Market,
    bool EntriesAllowed);

/// <summary>
/// The deterministic strategy engine (FR-STR-01): same candles and parameters, same output. Seeded by the agents
/// issue with an engine that reports no bias and no setup; the strategy issue provides the Appendix A engines.
/// Register an implementation before <c>AddAgents</c> to replace the default.
/// </summary>
public interface IStrategyEngine
{
    StrategyOutput Evaluate(StrategyContext context);
}
