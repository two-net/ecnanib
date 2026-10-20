using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Tests.Strategies.Catalog;
using Ecnanib.Tests.Strategies.Support;

namespace Ecnanib.Tests.Strategies.Engine;

/// <summary>Dispatch by the pinned (id, version) (FR-STR-06); a strategy's exception propagates; duplicates are refused.</summary>
public class StrategyEngineTests
{
    [Fact]
    public void DispatchesByIdAndVersionAndNotesAMissingVersion()
    {
        var strategies = StrategyCatalogTests.Strategies();
        var engine = new StrategyEngine(strategies);
        var harness = new StrategyHarness { Candles = [] };

        var known = engine.Evaluate(harness.Context(strategies[1]));
        Assert.Equal("trend-pullback", known.StrategyId);
        Assert.Equal(1, known.StrategyVersion);
        Assert.StartsWith("insufficient history", known.Note);

        harness.StrategyVersion = 2;
        var unknown = engine.Evaluate(harness.Context(strategies[1]));
        Assert.Equal(2, unknown.StrategyVersion);
        Assert.Null(unknown.Setup);
        Assert.Equal(BiasDirection.None, unknown.Bias);
        Assert.Contains("has no engine; upgrade the agent", unknown.Note);
    }

    [Fact]
    public void ExceptionsPropagateAndDuplicatesAreRefused()
    {
        var throwing = new ThrowingStrategy();
        var engine = new StrategyEngine([throwing]);
        var context = new StrategyHarness { Candles = [] }.Context(throwing);

        Assert.Throws<InvalidOperationException>(() => engine.Evaluate(context));
        Assert.Throws<InvalidOperationException>(() => new StrategyEngine([throwing, throwing]));
    }

    private sealed class ThrowingStrategy : IStrategy
    {
        public StrategyDescriptor Descriptor { get; } = new("throwing", "Throwing", CandleInterval.OneHour, 1, "Throws.", [], [], []);

        public StrategyOutput Evaluate(StrategyContext context) => throw new InvalidOperationException("boom");
    }
}
