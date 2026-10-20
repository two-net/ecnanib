using Ecnanib.Agents.Abstractions;
using Ecnanib.Strategies.Indicators;

namespace Ecnanib.Strategies.Engine;

/// <summary>Collects levels (in insertion order) and indicators (by ordinal key), rounded, into a <see cref="StrategyOutput"/> (FR-STR-02).</summary>
internal sealed class OutputBuilder
{
    private readonly List<PriceLevel> _levels = [];
    private readonly SortedDictionary<string, decimal> _indicators = new(StringComparer.Ordinal);

    public void Level(string name, decimal? price)
    {
        if (price is { } value)
            _levels.Add(new PriceLevel(name, Prices.Round(value)));
    }

    public void Indicator(string name, decimal? value)
    {
        if (value is { } number)
            _indicators[name] = Prices.Round(number);
    }

    public void Indicator(string name, int value) => _indicators[name] = value;

    public StrategyOutput Build(StrategyDescriptor descriptor, BiasDirection bias, StrategySetup? setup, string note) =>
        new(descriptor.Id, descriptor.Version, bias, _levels.ToList(), new Dictionary<string, decimal>(_indicators, StringComparer.Ordinal), setup, note);
}
