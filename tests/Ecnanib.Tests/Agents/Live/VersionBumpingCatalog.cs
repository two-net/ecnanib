using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>
/// The real catalog with some strategies reported at a newer version (FR-STR-06): the newer descriptor drops the given
/// parameters, while the real version stays reachable through <see cref="Find(string?, int)"/>.
/// </summary>
internal sealed class VersionBumpingCatalog : IStrategyCatalog
{
    private readonly IStrategyCatalog _inner;
    private readonly IReadOnlyDictionary<string, int> _versions;
    private readonly IReadOnlyCollection<string> _dropped;

    public VersionBumpingCatalog(IStrategyCatalog inner, IReadOnlyDictionary<string, int> versions, params string[] dropped)
    {
        _inner = inner;
        _versions = versions;
        _dropped = dropped;
    }

    public IReadOnlyList<StrategyDescriptor> All => _inner.All.Select(Bump).ToList();

    public IReadOnlyList<StrategyDescriptor> ForTimeframe(CandleInterval timeframe) => _inner.ForTimeframe(timeframe).Select(Bump).ToList();

    public StrategyDescriptor? Find(string? id) => _inner.Find(id) is { } descriptor ? Bump(descriptor) : null;

    public StrategyDescriptor? Find(string? id, int version)
    {
        if (_inner.Find(id) is { } latest && Bump(latest) is { } bumped && bumped.Version == version)
            return bumped;
        return _inner.Find(id, version);
    }

    private StrategyDescriptor Bump(StrategyDescriptor descriptor) => _versions.TryGetValue(descriptor.Id, out var version)
        ? descriptor with { Version = version, Parameters = descriptor.Parameters.Where(p => !_dropped.Contains(p.Name)).ToList() }
        : descriptor;
}
