using Ecnanib.Agents.Abstractions;

namespace Ecnanib.Strategies.Engine;

/// <summary>
/// Dispatches an evaluation to the strategy pinned by the agent, keyed by (id, version) so an older version can be
/// retained beside a newer one (FR-STR-06). A strategy's exception propagates: the pipeline records a failed
/// evaluation and trades nothing, which is safer than a silent "no setup".
/// </summary>
internal sealed class StrategyEngine : IStrategyEngine
{
    private readonly Dictionary<(string Id, int Version), IStrategy> _strategies = [];

    public StrategyEngine(IEnumerable<IStrategy> strategies)
    {
        foreach (var strategy in strategies)
        {
            var key = (strategy.Descriptor.Id, strategy.Descriptor.Version);
            if (!_strategies.TryAdd(key, strategy))
                throw new InvalidOperationException($"Strategy '{key.Id}' version {key.Version} is registered twice.");
        }
    }

    public StrategyOutput Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = (context.Strategy.Id, context.Strategy.Version);
        return _strategies.TryGetValue(key, out var strategy)
            ? strategy.Evaluate(context)
            : StrategyOutput.Nothing(context.Strategy, $"Strategy '{key.Id}' version {key.Version} has no engine; upgrade the agent.");
    }
}
