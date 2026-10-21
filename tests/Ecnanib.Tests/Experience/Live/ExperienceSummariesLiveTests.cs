using Ecnanib.Ai.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Experience.Live;

/// <summary>The summary a role prompt carries, from stored statistics and live outcomes (FR-EXP-06), scoped to the user (NFR-SEC-03).</summary>
[Trait("Category", "Live")]
public class ExperienceSummariesLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset Now = AgentsLiveSeed.Now;

    private readonly DatabaseLiveFixture _fixture;

    public ExperienceSummariesLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TellsTheThreeScopesAndTheRecentOutcomesOfTheUsersOwnAgents()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var userA = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-summary-a@example.com");
        var userB = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-summary-b@example.com");
        var userC = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-summary-c@example.com");
        var a1 = await AgentsLiveSeed.NewAgentAsync(_fixture, userA.Id, "A1", model: "c1");
        var b1 = await AgentsLiveSeed.NewAgentAsync(_fixture, userB.Id, "B1", model: "c1");
        var rm = (DecisionAction proposed) => ExperienceLiveSeed.Turn(CouncilRole.RiskManager, AiProvider.Claude, "c1", proposed);
        var ma = (DecisionAction proposed) => ExperienceLiveSeed.Turn(CouncilRole.MarketAnalyst, AiProvider.Gpt, "g1", proposed);
        var d1 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, Now.AddDays(-2), DecisionAction.OpenLong, [ma(DecisionAction.OpenLong), rm(DecisionAction.OpenLong)]);
        await ExperienceLiveSeed.NewClosedTradeAsync(_fixture, a1, d1, 100m, 90m, 120m, 1m, 2m, Now.AddDays(-1));
        var d2 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, a1, Now.AddDays(-3), DecisionAction.NoTrade, [ma(DecisionAction.OpenLong), rm(DecisionAction.NoTrade)]);
        await ExperienceLiveSeed.NewShadowAsync(_fixture, d2, Now.AddDays(-3), ShadowResult.HitStop, -1m);
        var d3 = await ExperienceLiveSeed.NewDecisionAsync(_fixture, b1, Now.AddDays(-2), DecisionAction.OpenLong, [rm(DecisionAction.OpenLong)]);
        await ExperienceLiveSeed.NewClosedTradeAsync(_fixture, b1, d3, 100m, 90m, 90m, 1m, 0m, Now.AddDays(-1), TradeExitReason.Stop);

        await using var provider = ExperienceTestHost.BuildServices(_fixture.ConnectionString, time: new FakeTimeProvider(Now));
        await provider.GetRequiredService<ExperienceRefresher>().RefreshOnceAsync(Ct);
        var summaries = provider.GetRequiredService<IExperienceSummaries>();

        var text = await summaries.SummarizeAsync(new ExperienceQuery(userA.Id, a1.Id, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot), Ct);

        Assert.NotNull(text);
        var lines = text.Split('\n');
        Assert.Equal("As Risk Manager on Claude c1 at 1h Spot:", lines[0]);
        Assert.Equal("- This agent: 2 turns (1 approval, 1 rejection, 0 failed, 1.0 s average); 1 trade: win rate 100%, average +2.00R, expectancy +1.80R net of fees; 1 rejection resolved, 100% avoided a loss; outputs clamped by the hard limits 0% of the time.", lines[1]);
        Assert.StartsWith("- All your agents: 2 turns (1 approval, 1 rejection", lines[2]);
        Assert.StartsWith("- All users (anonymous counts and ratios): 3 turns (2 approvals, 1 rejection, 0 failed, 1.0 s average); 2 trades: win rate 50%, average +0.50R", lines[3]);
        Assert.Equal("- Recent outcomes, newest first: 2026-01-04: +2.00R (target); 2026-01-02: rejected, would have hit the stop (right call).", lines[4]);
        Assert.DoesNotContain(userA.Id.ToString(), text);
        Assert.DoesNotContain("A1", text);

        var analyst = await summaries.SummarizeAsync(new ExperienceQuery(userA.Id, a1.Id, AiProvider.Gpt, "g1", CouncilRole.MarketAnalyst, CandleInterval.OneHour, MarketType.Spot), Ct);
        Assert.NotNull(analyst);
        Assert.Contains("- This agent: 2 turns (2 approvals, 0 rejections, 0 failed, 1.0 s average); 1 trade: win rate 100%", analyst);
        Assert.DoesNotContain("clamped", analyst);
        Assert.DoesNotContain("rejected", analyst);

        var other = await summaries.SummarizeAsync(new ExperienceQuery(userB.Id, b1.Id, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot), Ct);
        Assert.NotNull(other);
        Assert.Contains("- This agent: 1 turn (1 approval, 0 rejections, 0 failed, 1.0 s average); 1 trade: win rate 0%, average -1.00R, expectancy -1.00R net of fees", other);
        Assert.Contains("- Recent outcomes, newest first: 2026-01-04: -1.00R (stop).", other);

        // A user without any history sees the system-wide line only; a timeframe nobody traded sees nothing.
        var stranger = await summaries.SummarizeAsync(new ExperienceQuery(userC.Id, Guid.NewGuid(), AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.Spot), Ct);
        Assert.NotNull(stranger);
        Assert.DoesNotContain("This agent", stranger);
        Assert.DoesNotContain("All your agents", stranger);
        Assert.DoesNotContain("Recent outcomes", stranger);
        Assert.Contains("- All users (anonymous counts and ratios): 3 turns", stranger);
        Assert.Null(await summaries.SummarizeAsync(new ExperienceQuery(userA.Id, a1.Id, AiProvider.Claude, "c1", CouncilRole.RiskManager, CandleInterval.OneDay, MarketType.Spot), Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, userA.Id, userB.Id, userC.Id);
    }
}
