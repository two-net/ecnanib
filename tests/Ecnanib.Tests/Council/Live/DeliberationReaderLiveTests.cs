using Ecnanib.Ai.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Council.Live;

/// <summary>Token usage per agent (NFR-COST-01) against PostgreSQL: totals, today's share and the split per role, for the owner only.</summary>
[Trait("Category", "Live")]
public class DeliberationReaderLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public DeliberationReaderLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => AgentsLiveSeed.Ct;

    [Fact]
    public async Task UsageSumsEveryDecisionSplitsTodayAndEveryRoleAndStaysWithTheOwner()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "usage@example.com");
        var other = await AgentsLiveSeed.NewUserAsync(_fixture, "usage-other@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var now = AgentsLiveSeed.Now;
        var yesterday = now.AddDays(-1);
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(
                Decision(agent, now.AddHours(-1), now, 1200, 300,
                    Role(CouncilRole.MarketAnalyst, 1, RoleOutputStatus.Succeeded, 600, 150),
                    Role(CouncilRole.SentimentFlow, 2, RoleOutputStatus.Disabled, 0, 0),
                    Role(CouncilRole.RiskManager, 4, RoleOutputStatus.Succeeded, 600, 150)),
                Decision(agent, yesterday.AddHours(-1), yesterday, 1000, 200,
                    Role(CouncilRole.MarketAnalyst, 1, RoleOutputStatus.Failed, 500, 100),
                    Role(CouncilRole.CrossMarketContext, 3, RoleOutputStatus.Skipped, 0, 0),
                    Role(CouncilRole.RiskManager, 4, RoleOutputStatus.Succeeded, 500, 100)));
            await db.SaveChangesAsync(Ct);
        }

        var (provider, _, _, _, _) = CouncilLiveHost.Build(_fixture, new FakeAiClientFactory());
        await using var _ = provider;
        var reader = provider.GetRequiredService<IDeliberationReader>();

        var usage = await reader.GetAgentUsageAsync(user.Id, agent.Id, Ct);

        Assert.NotNull(usage);
        Assert.Equal((2, 2200, 500), (usage.Decisions, usage.InputTokens, usage.OutputTokens));
        Assert.Equal((1, 1200, 300), (usage.DecisionsToday, usage.InputTokensToday, usage.OutputTokensToday));
        Assert.Equal(yesterday, usage.Since);
        Assert.Equal(
        [
            new RoleTokenUsage(CouncilRole.MarketAnalyst, 2, 1100, 250),
            new RoleTokenUsage(CouncilRole.SentimentFlow, 0, 0, 0),
            new RoleTokenUsage(CouncilRole.CrossMarketContext, 0, 0, 0),
            new RoleTokenUsage(CouncilRole.RiskManager, 2, 1100, 250),
        ], usage.ByRole);

        var fresh = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "fresh");
        var none = await reader.GetAgentUsageAsync(user.Id, fresh.Id, Ct);
        Assert.NotNull(none);
        Assert.Equal((0, 0, 0, (DateTimeOffset?)null), (none.Decisions, none.InputTokens, none.OutputTokens, none.Since));
        Assert.All(none.ByRole, r => Assert.Equal(0, r.Calls));
        Assert.Null(await reader.GetAgentUsageAsync(other.Id, agent.Id, Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id, other.Id);
    }

    private static Decision Decision(Agent agent, DateTimeOffset candleOpen, DateTimeOffset createdAt, int input, int output, params DecisionRoleOutput[] roles) => new()
    {
        Evaluation = new Evaluation
        {
            AgentId = agent.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe, CandleOpenTime = candleOpen,
            StartedAt = createdAt.AddSeconds(-20), FinishedAt = createdAt, CouncilConsulted = true, Outcome = EvaluationOutcome.Decided,
        },
        FinalAction = DecisionAction.NoTrade, Rationale = "seeded", TotalInputTokens = input, TotalOutputTokens = output, TotalLatencyMs = 1000, CreatedAt = createdAt, RoleOutputs = roles,
    };

    private static DecisionRoleOutput Role(CouncilRole role, int sequence, RoleOutputStatus status, int input, int output) => new()
    {
        Role = role, Sequence = sequence, Provider = AiProvider.Gpt, Model = "m", Status = status, InputTokens = input, OutputTokens = output, LatencyMs = status == RoleOutputStatus.Succeeded ? 500 : 0,
    };
}
