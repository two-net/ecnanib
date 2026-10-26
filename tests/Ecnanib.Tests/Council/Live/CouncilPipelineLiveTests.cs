using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Agents.Services.Placeholders;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Council.Live;

/// <summary>The council inside the real pipeline against PostgreSQL (FR-AI-11, FR-AI-12, FR-AI-14, FR-SYNC-04), with the providers, the exchange and the streams faked.</summary>
[Trait("Category", "Live")]
public class CouncilPipelineLiveTests : IClassFixture<DatabaseLiveFixture>
{
    /// <summary>The 1H candle that closed five seconds before <see cref="AgentsLiveSeed.Now"/>.</summary>
    private static readonly DateTimeOffset LastOpen = new(2026, 1, 4, 23, 0, 0, TimeSpan.Zero);

    private readonly DatabaseLiveFixture _fixture;

    public CouncilPipelineLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARiskManagerFailurePersistsTheDeliberationAndLeavesProtectiveOrdersAlone()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "council-failed@example.com");
        var other = await AgentsLiveSeed.NewUserAsync(_fixture, "council-other@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var opening = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen.AddHours(-2));
        await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, opening);
        var ai = CouncilTestHost.Answering(overrides: new Dictionary<CouncilRole, string> { [CouncilRole.RiskManager] = "not a document" });
        var (provider, _, _, _, streams) = CouncilLiveHost.Build(_fixture, ai);
        await using var _ = provider;
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        var scheduler = provider.GetRequiredService<AgentScheduler>();

        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().Include(e => e.Decision).ThenInclude(d => d!.RoleOutputs).Include(e => e.Decision).ThenInclude(d => d!.Transcripts)
                .AsSplitQuery().SingleAsync(e => e.AgentId == agent.Id && e.CandleOpenTime == LastOpen, Ct);
            Assert.Equal(EvaluationOutcome.Failed, evaluation.Outcome);
            Assert.True(evaluation.CouncilConsulted);
            Assert.StartsWith("No decision: the Risk Manager failed: the answer was rejected twice", evaluation.OutcomeDetail);
            var decision = Assert.IsType<Decision>(evaluation.Decision);
            Assert.Equal(DecisionAction.NoTrade, decision.FinalAction);
            Assert.Equal(4, decision.RoleOutputs.Count);
            Assert.Equal(RoleOutputStatus.Failed, decision.RoleOutputs.Single(r => r.Role == CouncilRole.RiskManager).Status);
            Assert.Equal(3, decision.RoleOutputs.Count(r => r.Status == RoleOutputStatus.Succeeded));
            Assert.Equal(4, decision.Transcripts.Count);
            Assert.All(decision.Transcripts, t => Assert.Equal(AgentsLiveSeed.Now, t.CreatedAt));
            Assert.Equal(50, decision.TotalInputTokens);
            Assert.Null(decision.TradeId);
            Assert.Equal(2, await db.Orders.CountAsync(o => o.AgentId == agent.Id && o.Status == OrderStatus.New, Ct));
            Assert.Equal(0, await db.Orders.CountAsync(o => o.DecisionId == decision.Id, Ct));

            var reader = provider.GetRequiredService<IDeliberationReader>();
            var view = Assert.IsType<DeliberationView>(await reader.GetAsync(user.Id, evaluation.Id, Ct));
            Assert.Equal(agent.Id, view.AgentId);
            Assert.Equal("BTC", view.AgentName);
            Assert.Equal(EvaluationOutcome.Failed, view.Outcome);
            Assert.Equal([1, 2, 3, 4], view.Turns.Select(t => t.Sequence));
            Assert.Equal(RoleOutputStatus.Failed, view.Turns[3].Status);
            Assert.Equal(2, view.Turns[3].Attempts);
            Assert.Contains("rejected twice", view.Turns[3].Error);
            Assert.Null(view.Turns[3].Document);
            Assert.NotNull(view.Turns[0].Document);
            Assert.Contains("## Preceding roles", view.Turns[3].InputSummary);
            Assert.Equal(DecisionAction.NoTrade, view.Decision!.Action);
            Assert.Empty(view.Decision.Limits);
            Assert.Null(await reader.GetAsync(other.Id, evaluation.Id, Ct));
            Assert.Null(await reader.GetAsync(user.Id, Guid.NewGuid(), Ct));
        }

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id, other.Id);
    }

    [Fact]
    public async Task ADecisionIsPersistedWithItsLimitsCheckAndHandedToExecution()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "council-decided@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running, discretionary: true);
        var ai = CouncilTestHost.Answering(CouncilTestHost.FinalJson("open_long", "limit", 100m, 96m, [108m], risk: 5m, rationale: "Discretionary long."));
        var (provider, logs, _, _, streams) = CouncilLiveHost.Build(_fixture, ai, configure: s => s.Replace(ServiceDescriptor.Singleton<IStrategyEngine>(new AtrOnlyEngine())));
        await using var _ = provider;
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        var scheduler = provider.GetRequiredService<AgentScheduler>();

        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().Include(e => e.Decision).ThenInclude(d => d!.RoleOutputs).SingleAsync(e => e.AgentId == agent.Id && e.CandleOpenTime == LastOpen, Ct);
            Assert.Equal(EvaluationOutcome.Decided, evaluation.Outcome);
            Assert.Equal(PendingExecutionService.Note, evaluation.OutcomeDetail);
            var decision = Assert.IsType<Decision>(evaluation.Decision);
            Assert.Equal(DecisionAction.OpenLong, decision.FinalAction);
            Assert.Equal(OrderType.Limit, decision.EntryType);
            Assert.Equal(100m, decision.EntryPrice);
            Assert.Equal(96m, decision.StopPrice);
            Assert.Equal(1m, decision.RiskPercent);
            Assert.Null(decision.PartialFraction);
            Assert.Null(decision.Size);
            Assert.Equal("Discretionary long.", decision.Rationale);
            Assert.Equal(40, decision.TotalInputTokens);
            Assert.Equal(20, decision.TotalOutputTokens);
            var checks = decision.LimitsCheck!.Value.EnumerateArray().ToDictionary(c => c.GetProperty("limit").GetString()!, c => c.GetProperty("status").GetString());
            Assert.Equal("clamped", checks[LimitNames.RiskPerTrade]);
            Assert.Equal("pass", checks[LimitNames.SetupRequired]);
            Assert.Equal("pass", checks[LimitNames.StopDistance]);
            Assert.All(decision.RoleOutputs, r => Assert.Equal(RoleOutputStatus.Succeeded, r.Status));
            Assert.All(decision.RoleOutputs, r => Assert.Equal("test-model", r.Model));
        }

        Assert.Contains(logs.Entries, e => e.Message.Contains("Council decided OpenLong") && e.Message.Contains("riskPerTrade clamped"));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AgentsLiveSeed.ProviderApiKey));
        Assert.Equal(4, ai.Completions.Count);
        Assert.All(ai.Completions, c => Assert.DoesNotContain(AgentsLiveSeed.ProviderApiKey, c.Request.UserPrompt));

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AModelTheProviderRejectsMovesTheAgentToError()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "council-fault@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running, discretionary: true);
        var ai = CouncilTestHost.Answering();
        var inner = ai.OnComplete!;
        ai.OnComplete = (key, request, ct) => request.Purpose == "MarketAnalyst"
            ? throw new AiRequestException("The model `test-model` does not exist.", AiProvider.Gpt, AiErrorKind.UnknownModel, 404, "model_not_found", "test-model")
            : inner(key, request, ct);
        var (provider, _, _, _, streams) = CouncilLiveHost.Build(_fixture, ai);
        await using var _ = provider;
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        var scheduler = provider.GetRequiredService<AgentScheduler>();

        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");
        await Wait.UntilAsync(() => !scheduler.IsTracked(agent.Id), "untracked");
        await Wait.UntilAsync(() => State(agent.Id) == AgentState.Error, "in Error");

        Assert.Equal(EvaluationOutcome.Failed, Outcome(agent.Id, LastOpen));
        Assert.StartsWith("No decision: the Risk Manager skipped: the council stopped: The Market Analyst role's model 'test-model' was rejected by GPT", Detail(agent.Id, LastOpen));
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(AgentState.Error, row.State);
        Assert.Equal("The Market Analyst role's model 'test-model' was rejected by GPT: The model `test-model` does not exist.", row.StateReason);
        Assert.Single(ai.Completions);
        var changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Contains(changes, c => c.Field == "state" && c.NewValue!.Value.GetRawText().Contains("Error", StringComparison.Ordinal));

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private EvaluationOutcome? Outcome(Guid agentId, DateTimeOffset candleOpen)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId && e.CandleOpenTime == candleOpen && e.FinishedAt != null).Select(e => (EvaluationOutcome?)e.Outcome).SingleOrDefault();
    }

    private AgentState State(Guid agentId)
    {
        using var db = _fixture.CreateContext();
        return db.Agents.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.State).Single();
    }

    private string? Detail(Guid agentId, DateTimeOffset candleOpen)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId && e.CandleOpenTime == candleOpen).Select(e => e.OutcomeDetail).SingleOrDefault();
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
