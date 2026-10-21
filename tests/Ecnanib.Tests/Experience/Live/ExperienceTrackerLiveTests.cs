using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Council;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Experience.Live;

/// <summary>Shadow outcomes opened and advanced by the real pipeline (FR-EXP-03), with a strategy engine that always reports a long setup and providers that reject it.</summary>
[Trait("Category", "Live")]
public class ExperienceTrackerLiveTests : IClassFixture<DatabaseLiveFixture>
{
    /// <summary>The 1H candle that closed five seconds before <see cref="AgentsLiveSeed.Now"/>.</summary>
    private static readonly DateTimeOffset LastOpen = new(2026, 1, 4, 23, 0, 0, TimeSpan.Zero);

    private readonly DatabaseLiveFixture _fixture;

    public ExperienceTrackerLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AVetoedSetupOpensAShadowThatTheNextCandlesResolve()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-track-veto@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var candles = new List<Candle>();
        var (provider, logs, time, exchange, streams) = Build(CouncilTestHost.Answering(), candles);
        await using var _ = provider;
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        candles.AddRange(AgentsLiveSeed.RecentCandles("BTCUSDT", CandleInterval.OneHour, 3, time.GetUtcNow()));
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "first evaluation");

        Assert.Equal(EvaluationOutcome.Decided, Outcome(agent.Id, LastOpen));
        var first = await DecisionAsync(agent.Id, LastOpen);
        Assert.Equal(DecisionAction.NoTrade, first.FinalAction);
        var shadow = await ExperienceLiveSeed.ShadowAsync(_fixture, first.Id);
        Assert.Equal(ShadowResult.Pending, shadow.Result);
        var detail = Assert.IsType<ShadowDetail>(ShadowDetail.Parse(shadow.Detail));
        Assert.Equal((ShadowDetail.Veto, PositionSide.Long, OrderType.Limit, 100m, 96m, 108m, LastOpen, 1, 30), (detail.Reason, detail.Side, detail.EntryType, detail.Entry, detail.Stop, detail.Target, detail.DecisionCandleOpenTime, detail.EntryTimeoutCandles, detail.HorizonCandles));
        Assert.Equal(0, detail.CandlesSeen);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith($"Shadow outcome opened for decision {first.Id}", StringComparison.Ordinal));

        // The next candle dips to the entry and runs through the target: the shadow fills at 100 and wins +2R.
        time.Advance(TimeSpan.FromHours(1));
        var next = LastOpen.AddHours(1);
        candles.Add(new Candle("BTCUSDT", CandleInterval.OneHour, next, next.AddHours(1).AddMilliseconds(-1), 100.5m, 110m, 99m, 108m, 100m, 10_000m, 10, true));
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, candles[^1]);
        await Wait.UntilAsync(() => Outcome(agent.Id, next) is not null, "second evaluation");

        var resolved = await ExperienceLiveSeed.ShadowAsync(_fixture, first.Id);
        Assert.Equal(ShadowResult.HitTarget, resolved.Result);
        Assert.Equal(2m, resolved.RMultiple);
        Assert.Equal(time.GetUtcNow(), resolved.ResolvedAt);
        var walked = Assert.IsType<ShadowDetail>(ShadowDetail.Parse(resolved.Detail));
        Assert.Equal((ShadowDetail.ByTarget, next, 100m, next, 1), (walked.Resolution, walked.FilledAt, walked.FillPrice, walked.ResolvedCandleOpenTime, walked.CandlesSeen));
        var second = await DecisionAsync(agent.Id, next);
        Assert.Equal(ShadowResult.Pending, (await ExperienceLiveSeed.ShadowAsync(_fixture, second.Id)).Result);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith($"Shadow outcome of decision {first.Id} for agent {agent.Id} resolved HitTarget (target) at 2R", StringComparison.Ordinal));
        Assert.Equal(2, exchange.RecentCandleReads.Count(r => r.Interval == CandleInterval.OneHour && r.Symbol == "BTCUSDT"));

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ARiskManagerFailureOnASetupOpensANoDecisionShadow()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-track-failed@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var candles = AgentsLiveSeed.RecentCandles("BTCUSDT", CandleInterval.OneHour, 3, AgentsLiveSeed.Now).ToList();
        var (provider, _, _, _, streams) = Build(CouncilTestHost.Answering(overrides: new Dictionary<CouncilRole, string> { [CouncilRole.RiskManager] = "not a document" }), candles);
        await using var _ = provider;
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        Assert.Equal(EvaluationOutcome.Failed, Outcome(agent.Id, LastOpen));
        var decision = await DecisionAsync(agent.Id, LastOpen);
        var shadow = await ExperienceLiveSeed.ShadowAsync(_fixture, decision.Id);
        Assert.Equal(ShadowResult.Pending, shadow.Result);
        Assert.Equal(ShadowDetail.NoDecision, ShadowDetail.Parse(shadow.Detail)!.Reason);

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AFailingTrackerIsLoggedAndLeavesTheEvaluationAlone()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exp-track-throws@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var candles = AgentsLiveSeed.RecentCandles("BTCUSDT", CandleInterval.OneHour, 3, AgentsLiveSeed.Now).ToList();
        var (provider, logs, _, _, streams) = Build(CouncilTestHost.Answering(), candles, s => s.AddSingleton<IExperienceTracker>(new ThrowingTracker()));
        await using var _ = provider;
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        Assert.Equal(EvaluationOutcome.Decided, Outcome(agent.Id, LastOpen));
        var decision = await DecisionAsync(agent.Id, LastOpen);
        await using (var db = _fixture.CreateContext())
            Assert.False(await db.ShadowOutcomes.AnyAsync(s => s.DecisionId == decision.Id, Ct));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Experience tracking for evaluation", StringComparison.Ordinal) && e.Exception is InvalidOperationException);

        await scheduler.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private (ServiceProvider Provider, TestLoggerProvider Logs, FakeTimeProvider Time, FakeExchangeClientFactory Exchange, FakeMarketStreams Streams) Build(
        FakeAiClientFactory ai, List<Candle> candles, Action<IServiceCollection>? configure = null)
    {
        var logs = new TestLoggerProvider();
        var time = new FakeTimeProvider(AgentsLiveSeed.Now);
        var exchange = new FakeExchangeClientFactory
        {
            OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT", "ETHUSDT"),
            OnRecentCandles = (symbol, interval, count) => interval == CandleInterval.OneHour ? candles.ToList() : AgentsLiveSeed.RecentCandles(symbol, interval, count, time.GetUtcNow()),
        };
        var streams = new FakeMarketStreams();
        var provider = ExperienceTestHost.BuildServices(_fixture.ConnectionString, logs, null, time, exchange, ai, streams, new AlwaysLongSetup(), configure);
        return (provider, logs, time, exchange, streams);
    }

    private EvaluationOutcome? Outcome(Guid agentId, DateTimeOffset candleOpen)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId && e.CandleOpenTime == candleOpen && e.FinishedAt != null).Select(e => (EvaluationOutcome?)e.Outcome).SingleOrDefault();
    }

    private async Task<Decision> DecisionAsync(Guid agentId, DateTimeOffset candleOpen)
    {
        await using var db = _fixture.CreateContext();
        return await db.Decisions.AsNoTracking().Include(d => d.RoleOutputs).SingleAsync(d => d.Evaluation.AgentId == agentId && d.Evaluation.CandleOpenTime == candleOpen, Ct);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    /// <summary>The Appendix A long setup of the council tests on every candle while entries are allowed (as the real engine, nothing when paused).</summary>
    private sealed class AlwaysLongSetup : IStrategyEngine
    {
        public StrategyOutput Evaluate(StrategyContext context) =>
            CouncilTestHost.Output(context.EntriesAllowed ? CouncilTestHost.LongSetup() : null) with { StrategyId = context.Strategy.Id, StrategyVersion = context.Strategy.Version };
    }

    private sealed class ThrowingTracker : IExperienceTracker
    {
        public Task TrackAsync(ExperienceContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("tracker down");
    }
}
