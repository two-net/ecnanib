using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Experience.Live;

/// <summary>Council suggestions from the user's own statistics and their use at agent creation (FR-EXP-08).</summary>
[Trait("Category", "Live")]
public class CouncilSuggestionsLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset Now = AgentsLiveSeed.Now;

    private readonly DatabaseLiveFixture _fixture;

    public CouncilSuggestionsLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreationTakesTheBestExpectancyPerRoleAndFallsBackToAppendixB()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var userA = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-suggest-a@example.com");
        var userB = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-suggest-b@example.com");
        await ExperienceLiveSeed.AddCatalogModelsAsync(_fixture, Now,
            (AiProvider.Claude, "c-new", ModelStatus.Available), (AiProvider.Gpt, "g-new", ModelStatus.Available), (AiProvider.Gemini, "m-new", ModelStatus.Available),
            (AiProvider.Grok, "k-new", ModelStatus.Available), (AiProvider.Grok, "k1", ModelStatus.Deprecated));
        await Stat(userA.Id, AiProvider.Claude, "c1", CouncilRole.RiskManager, 0.5m, 12);
        await Stat(userA.Id, AiProvider.Gpt, "g1", CouncilRole.RiskManager, 0.8m, 3);
        await Stat(userA.Id, AiProvider.Grok, "k1", CouncilRole.MarketAnalyst, 0.9m, 15);
        await Stat(userA.Id, AiProvider.Gemini, "m1", CouncilRole.CrossMarketContext, 0.1m, 10);
        await Stat(userB.Id, AiProvider.Gpt, "g9", CouncilRole.RiskManager, 5m, 50);

        var logs = new TestLoggerProvider();
        var exchange = new FakeExchangeClientFactory { OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT") };
        await using var provider = ExperienceTestHost.BuildServices(_fixture.ConnectionString, logs, time: new FakeTimeProvider(Now), exchange: exchange);

        var suggestions = await provider.GetRequiredService<ICouncilSuggestions>().SuggestAsync(userA.Id, CandleInterval.OneHour, MarketType.Spot, Ct);
        Assert.Equal([new CouncilSuggestion(CouncilRole.CrossMarketContext, AiProvider.Gemini, "m1", 0.1m, 10), new CouncilSuggestion(CouncilRole.RiskManager, AiProvider.Claude, "c1", 0.5m, 12)], suggestions);
        Assert.Empty(await provider.GetRequiredService<ICouncilSuggestions>().SuggestAsync(userA.Id, CandleInterval.FourHours, MarketType.Spot, Ct));

        var agents = provider.GetRequiredService<IAgentService>();
        var created = await agents.CreateAsync(userA.Id, new AgentCreateInput("exp", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, created.Status);
        var agent = await AgentsLiveSeed.AgentAsync(_fixture, created.AgentId!.Value);
        Assert.Equal((AiProvider.Gpt, "g-new"), Assignment(agent, CouncilRole.MarketAnalyst));
        Assert.Equal((AiProvider.Grok, "k-new"), Assignment(agent, CouncilRole.SentimentFlow));
        Assert.Equal((AiProvider.Gemini, "m1"), Assignment(agent, CouncilRole.CrossMarketContext));
        Assert.Equal((AiProvider.Claude, "c1"), Assignment(agent, CouncilRole.RiskManager));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("2 council role(s) assigned from experience", StringComparison.Ordinal));

        var reset = await agents.ResetCouncilToDefaultsAsync(userA.Id, agent.Id, Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, reset.Status);
        var defaults = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal((AiProvider.Claude, "c-new"), Assignment(defaults, CouncilRole.RiskManager));
        Assert.Equal((AiProvider.Gemini, "m-new"), Assignment(defaults, CouncilRole.CrossMarketContext));

        var daily = await agents.CreateAsync(userA.Id, new AgentCreateInput("exp-4h", MarketType.Spot, "BTCUSDT", CandleInterval.FourHours, AgentMode.Paper, null, null, null), Ct);
        var plain = await AgentsLiveSeed.AgentAsync(_fixture, daily.AgentId!.Value);
        Assert.Equal((AiProvider.Claude, "c-new"), Assignment(plain, CouncilRole.RiskManager));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("0 council role(s) assigned from experience", StringComparison.Ordinal));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, userA.Id, userB.Id);
    }

    private Task Stat(Guid userId, AiProvider provider, string model, CouncilRole role, decimal expectancy, int trades) =>
        ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.User, userId, null, provider, model, role, CandleInterval.OneHour, MarketType.Spot,
            new ExperienceMetrics { Decisions = trades, Approvals = trades, Trades = trades, Wins = trades, SumR = expectancy * trades, GrossProfitR = expectancy * trades, NetTrades = trades, SumNetR = expectancy * trades }, Now);

    private static (AiProvider Provider, string Model) Assignment(Agent agent, CouncilRole role)
    {
        var assignment = agent.CouncilAssignments.Single(c => c.Role == role);
        return (assignment.Provider, assignment.Model);
    }
}
