using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A as descriptors, built from the registered strategies: the newest version of each for listing, every version for the pinned lookup (FR-STR-06).</summary>
internal sealed class StrategyCatalog : IStrategyCatalog
{
    private readonly IReadOnlyList<StrategyDescriptor> _latest;
    private readonly Dictionary<(string Id, int Version), StrategyDescriptor> _versions = [];

    public StrategyCatalog(IEnumerable<IStrategy> strategies)
    {
        var all = strategies.Select(s => s.Descriptor).ToList();
        foreach (var descriptor in all)
        {
            if (!_versions.TryAdd((descriptor.Id, descriptor.Version), descriptor))
                throw new InvalidOperationException($"Strategy '{descriptor.Id}' version {descriptor.Version} is registered twice.");
        }

        _latest = all.GroupBy(d => d.Id, StringComparer.Ordinal).Select(g => g.OrderByDescending(d => d.Version).First()).ToList();
    }

    public IReadOnlyList<StrategyDescriptor> All => _latest;

    public IReadOnlyList<StrategyDescriptor> ForTimeframe(CandleInterval timeframe) => _latest.Where(s => s.Timeframe == timeframe).ToList();

    public StrategyDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _latest.FirstOrDefault(s => string.Equals(s.Id, id.Trim(), StringComparison.Ordinal));

    public StrategyDescriptor? Find(string? id, int version) =>
        !string.IsNullOrWhiteSpace(id) && _versions.TryGetValue((id.Trim(), version), out var descriptor) ? descriptor : null;
}
