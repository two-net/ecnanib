using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Microsoft.Extensions.Options;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Strategies.Support;

/// <summary>Builds a <see cref="StrategyContext"/> from candle series, parameter overrides and an agent, and evaluates a strategy through the engine as the pipeline would.</summary>
internal sealed class StrategyHarness
{
    /// <summary>The pipeline's serializer: web defaults and string enums.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static IOptions<AgentsOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new AgentsOptions());

    public string Symbol { get; set; } = "ETHUSDT";

    public MarketType MarketType { get; set; } = MarketType.UsdFutures;

    public CandleInterval Timeframe { get; set; } = CandleInterval.OneHour;

    public int StrategyVersion { get; set; } = 1;

    public List<EntryWindow> EntryWindows { get; } = [];

    public Dictionary<string, JsonElement> Overrides { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Candle> Candles { get; set; } = [];

    public Dictionary<CandleInterval, IReadOnlyList<Candle>> Higher { get; } = [];

    public Dictionary<ReferenceSeries, IReadOnlyList<Candle>> References { get; } = [];

    public MarkPrice? Mark { get; set; }

    public bool EntriesAllowed { get; set; } = true;

    public StrategyHarness With(string name, object value)
    {
        Overrides[name] = JsonSerializer.SerializeToElement(value);
        return this;
    }

    public StrategyContext Context(IStrategy strategy)
    {
        var agent = new Agent
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            UserId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Name = "test",
            MarketType = MarketType,
            Symbol = Symbol,
            Timeframe = Timeframe,
            StrategyId = strategy.Descriptor.Id,
            StrategyVersion = StrategyVersion,
            State = EntriesAllowed ? AgentState.Running : AgentState.Paused,
        };
        foreach (var window in EntryWindows)
            agent.EntryWindows.Add(window);
        var descriptor = strategy.Descriptor with { Version = StrategyVersion };
        var parameters = descriptor.Resolve(Overrides);
        var market = new MarketSnapshot(Candles, Higher, References, Mark);
        return new StrategyContext(agent, descriptor, parameters, market, EntriesAllowed);
    }

    public StrategyOutput Evaluate(IStrategy strategy) => new StrategyEngine([strategy]).Evaluate(Context(strategy));

    public string EvaluateJson(IStrategy strategy) => JsonSerializer.Serialize(Evaluate(strategy), Json);

    /// <summary>The same harness with every series reflected about <paramref name="pivot"/> and the funding rate negated.</summary>
    public StrategyHarness Mirrored(decimal pivot)
    {
        var mirrored = new StrategyHarness
        {
            Symbol = Symbol, MarketType = MarketType, Timeframe = Timeframe, StrategyVersion = StrategyVersion, EntriesAllowed = EntriesAllowed,
            Candles = CandleSeries.Mirror(Candles, pivot),
            Mark = Mark is null ? null : Mark with { Mark = 2 * pivot - Mark.Mark, FundingRate = -Mark.FundingRate },
        };
        foreach (var (name, value) in Overrides)
            mirrored.Overrides[name] = value;
        foreach (var window in EntryWindows)
            mirrored.EntryWindows.Add(window);
        foreach (var (interval, candles) in Higher)
            mirrored.Higher[interval] = CandleSeries.Mirror(candles, pivot);
        foreach (var (series, candles) in References)
            mirrored.References[series] = CandleSeries.Mirror(candles, pivot);
        return mirrored;
    }
}
