using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;

namespace Ecnanib.Tests.Experience.Services;

/// <summary>Which provider and model a role is suggested (FR-EXP-08).</summary>
public class CouncilSuggestionsTests
{
    [Fact]
    public void TheBestExpectancyWithEnoughTradesWinsPerRoleAndDeprecatedModelsAreSkipped()
    {
        var rows = new[]
        {
            Row(CouncilRole.RiskManager, AiProvider.Claude, "c1", 0.5m, 12),
            Row(CouncilRole.RiskManager, AiProvider.Gpt, "g1", 0.8m, 3),
            Row(CouncilRole.RiskManager, AiProvider.Gemini, "m1", 0.5m, 20),
            Row(CouncilRole.MarketAnalyst, AiProvider.Grok, "k1", 0.9m, 15),
            Row(CouncilRole.MarketAnalyst, AiProvider.Gpt, "g1", 0.2m, 15),
            Row(CouncilRole.SentimentFlow, AiProvider.Grok, "k1", null, 15),
        };

        var suggestions = CouncilSuggestions.Choose(rows, new HashSet<(AiProvider, string)> { (AiProvider.Grok, "k1") }, 10);

        Assert.Equal([CouncilRole.MarketAnalyst, CouncilRole.RiskManager], suggestions.Select(s => s.Role));
        Assert.Equal(new CouncilSuggestion(CouncilRole.MarketAnalyst, AiProvider.Gpt, "g1", 0.2m, 15), suggestions[0]);
        Assert.Equal(new CouncilSuggestion(CouncilRole.RiskManager, AiProvider.Gemini, "m1", 0.5m, 20), suggestions[1]);
    }

    [Fact]
    public void NothingQualifiesWithoutTheMinimumSample() =>
        Assert.Empty(CouncilSuggestions.Choose([Row(CouncilRole.RiskManager, AiProvider.Claude, "c1", 1m, 9)], new HashSet<(AiProvider, string)>(), 10));

    private static ExperienceStat Row(CouncilRole role, AiProvider provider, string model, decimal? expectancy, int trades) => new()
    {
        Scope = StatScope.User, UserId = Guid.NewGuid(), Provider = provider, Model = model, Role = role, Timeframe = CandleInterval.OneHour, MarketType = MarketType.Spot,
        Metrics = new ExperienceMetrics { Expectancy = expectancy, Trades = trades },
    };
}
