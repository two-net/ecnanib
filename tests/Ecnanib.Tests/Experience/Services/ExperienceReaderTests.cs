using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Abstractions;
using Ecnanib.Experience.Services;

namespace Ecnanib.Tests.Experience.Services;

/// <summary>Re-aggregation over the filter and the leaderboard order (FR-EXP-07).</summary>
public class ExperienceReaderTests
{
    [Fact]
    public void AggregatingAcrossTimeframesSumsTheCountsAndCountsEachLossDenominatorOnce()
    {
        var rows = new[]
        {
            Row("c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, trades: 2, sumR: 1m, grossLoss: 1m, scopeLoss: 4m),
            Row("c1", CouncilRole.MarketAnalyst, CandleInterval.OneHour, MarketType.Spot, trades: 2, sumR: 1m, grossLoss: 1m, scopeLoss: 4m),
            Row("c1", CouncilRole.RiskManager, CandleInterval.OneDay, MarketType.Spot, trades: 1, sumR: 3m, grossLoss: 0m, scopeLoss: 2m),
        };

        var metrics = ExperienceReader.Aggregate(rows);

        Assert.Equal(5, metrics.Trades);
        Assert.Equal(5m, metrics.SumR);
        Assert.Equal(1m, metrics.AverageR);
        Assert.Equal(6m, metrics.ScopeGrossLossR);
        Assert.Equal(0.3333m, metrics.DrawdownContribution);
    }

    [Fact]
    public void RankOrdersByExpectancyThenSampleAndGroupsByProviderAndModel()
    {
        var rows = new[]
        {
            Row("c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, trades: 4, sumR: 2m, netTrades: 4, sumNetR: 1.6m),
            Row("c1", CouncilRole.MarketAnalyst, CandleInterval.OneHour, MarketType.Spot, trades: 4, sumR: 2m, netTrades: 4, sumNetR: 1.6m),
            Row("g1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, trades: 10, sumR: 8m, netTrades: 10, sumNetR: 4m, provider: AiProvider.Gpt),
            Row("k1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, trades: 0, provider: AiProvider.Grok, decisions: 50),
            Row("m1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot, trades: 2, sumR: 1m, netTrades: 2, sumNetR: 0.8m, provider: AiProvider.Gemini),
        };

        var ranked = ExperienceReader.Rank(rows);

        Assert.Equal(["g1", "c1", "m1", "k1"], ranked.Select(r => r.Model));
        Assert.Equal(8, ranked[1].Metrics.Trades);
        Assert.Equal(0.4m, ranked[0].Metrics.Expectancy);
        Assert.Null(ranked[3].Metrics.Expectancy);
    }

    [Fact]
    public void TheFilterMatchesOnlyTheDimensionsItSets()
    {
        var row = Row("c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot);

        Assert.True(ExperienceFilter.None.Matches(row));
        Assert.True(new ExperienceFilter(CouncilRole.RiskManager, null, MarketType.Spot).Matches(row));
        Assert.False(new ExperienceFilter(CouncilRole.MarketAnalyst, null, null).Matches(row));
        Assert.False(new ExperienceFilter(null, CandleInterval.OneDay, null).Matches(row));
        Assert.False(new ExperienceFilter(null, null, MarketType.UsdFutures).Matches(row));
    }

    private static ExperienceStat Row(string model, CouncilRole role, CandleInterval timeframe, MarketType market, int trades = 0, decimal sumR = 0m, decimal grossLoss = 0m,
        decimal scopeLoss = 0m, int netTrades = 0, decimal sumNetR = 0m, AiProvider provider = AiProvider.Claude, int decisions = 1) => new()
    {
        Scope = StatScope.User, UserId = Guid.NewGuid(), Provider = provider, Model = model, Role = role, Timeframe = timeframe, MarketType = market,
        Metrics = new ExperienceMetrics { Decisions = decisions, Trades = trades, SumR = sumR, GrossProfitR = Math.Max(0, sumR + grossLoss), GrossLossR = grossLoss, ScopeGrossLossR = scopeLoss, NetTrades = netTrades, SumNetR = sumNetR }.Derive(),
    };
}
