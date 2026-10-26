using Ecnanib.Accounts.Abstractions;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Agents.Services.Paper;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Council;
using Ecnanib.Tests.Council.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>FR-AGT-10 end to end with the council faked and execution registered: a decision becomes an entry, a fill, units, a partial and a breakeven stop at the next close; the kill switches (FR-EXE-10); one live agent per key, market and symbol (FR-EXE-08).</summary>
[Trait("Category", "Live")]
public class ExecutionPipelineLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset LastOpen = ExecutionLiveSeed.LastOpen;

    private readonly DatabaseLiveFixture _fixture;

    public ExecutionPipelineLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    [Fact]
    public async Task ADecisionIsSizedPlacedFilledTakenPartiallyAndMovedToBreakevenAtTheNextClose()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "pipeline-exec@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running, discretionary: true);
        var ai = CouncilTestHost.Answering(CouncilTestHost.FinalJson("open_long", "limit", 100m, 96m, [104m, 108m], risk: 1m, rationale: "Discretionary long."));
        var (provider, logs, _, _, streams) = CouncilLiveHost.Build(_fixture, ai, new() { ["Execution:FillTrackerStartDelaySeconds"] = "0" }, execution: true,
            configure: s => s.Replace(ServiceDescriptor.Singleton<IStrategyEngine>(new AtrOnlyEngine())));
        await using var _ = provider;
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var simulator = provider.GetRequiredService<PaperFillSimulator>();
        await simulator.StartAsync(Ct);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");

        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen));
        await Wait.UntilAsync(() => Outcome(agent.Id, LastOpen) is not null, "evaluated");

        Evaluation first;
        await using (var db = _fixture.CreateContext())
        {
            first = await db.Evaluations.AsNoTracking().Include(e => e.Decision).SingleAsync(e => e.AgentId == agent.Id && e.CandleOpenTime == LastOpen, Ct);
            Assert.Equal(EvaluationOutcome.Decided, first.Outcome);
            Assert.Equal("Limit long 25 BTCUSDT at 100 (stop 96), expires 01:00 UTC.", first.OutcomeDetail);
            Assert.Equal(25m, first.Decision!.Size);
            Assert.Equal("pass", ExecutionLiveSeed.Checks(first.Decision)["balance"]);
            var entry = await db.Orders.AsNoTracking().SingleAsync(o => o.AgentId == agent.Id, Ct);
            Assert.Equal((OrderPurpose.Entry, OrderType.Limit, 100m, 96m, 25m, OrderStatus.New), (entry.Purpose, entry.Type, entry.Price, entry.StopPrice, entry.Quantity, entry.Status));
            Assert.Equal(LastOpen.AddHours(2), entry.ExpiresAt);
        }

        // The limit fills, the units take their targets: the partial at 104 leaves the rest open.
        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 100m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => RemainingSize(agent.Id) == 25m, "filled");
        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 104m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => RemainingSize(agent.Id) == 16.75m, "partial taken");
        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.AgentId == agent.Id, Ct);
            Assert.Equal((25m, 16.75m, 96m), (trade.Size, trade.RemainingSize, trade.CurrentStop));
            Assert.Null(trade.ClosedAt);
            var open = await db.Orders.AsNoTracking().Where(o => o.AgentId == agent.Id && o.Status == OrderStatus.New).ToListAsync(Ct);
            Assert.Equal(16.75m, Assert.Single(open, o => o.Purpose == OrderPurpose.StopLoss).Quantity);
            Assert.Equal((108m, 16.75m), (Assert.Single(open, o => o.Purpose == OrderPurpose.TakeProfit).Price, open.Single(o => o.Purpose == OrderPurpose.TakeProfit).Quantity));
        }

        // The next close: management moves the stop to breakeven before the council, whose repeated entry the position cap rejects.
        var next = LastOpen.AddHours(1);
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, next, close: 104m));
        await Wait.UntilAsync(() => Outcome(agent.Id, next) is not null, "evaluated again");
        await using (var db = _fixture.CreateContext())
        {
            var second = await db.Evaluations.AsNoTracking().Include(e => e.Decision).SingleAsync(e => e.AgentId == agent.Id && e.CandleOpenTime == next, Ct);
            Assert.Equal(EvaluationOutcome.Decided, second.Outcome);
            Assert.StartsWith("Stop breakeven: 100.", second.OutcomeDetail);
            Assert.Equal(DecisionAction.NoTrade, second.Decision!.FinalAction);
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.AgentId == agent.Id, Ct);
            Assert.Equal(100m, trade.CurrentStop);
            Assert.Contains("\"breakeven\"", trade.StopHistory.GetRawText());
            Assert.Equal(100m, (await db.Orders.AsNoTracking().SingleAsync(o => o.AgentId == agent.Id && o.Purpose == OrderPurpose.StopLoss && o.Status == OrderStatus.New, Ct)).StopPrice);
        }

        Assert.Contains(logs.Entries, e => e.Message.Contains("Limit long 25 BTCUSDT"));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AgentsLiveSeed.ProviderApiKey));

        await scheduler.StopAsync(Ct);
        await simulator.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task StopAllStopsEveryAgentThatCanBeStoppedAndReportsTheRest()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "pipeline-stopall@example.com");
        var running = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "running", AgentState.Running);
        var paused = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "paused", AgentState.Paused, symbol: "ETHUSDT");
        var draft = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "draft", AgentState.Draft);
        var opening = await AgentsLiveSeed.NewDecisionAsync(_fixture, running, LastOpen);
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, running, opening);
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var result = await agents.StopAllAsync(user.Id, closePositions: false, Ct);

        Assert.Equal(2, result.Stopped);
        Assert.Empty(result.Failed);
        Assert.Equal(AgentState.Stopped, (await AgentsLiveSeed.AgentAsync(_fixture, running.Id)).State);
        Assert.Equal(AgentState.Stopped, (await AgentsLiveSeed.AgentAsync(_fixture, paused.Id)).State);
        Assert.Equal(AgentState.Draft, (await AgentsLiveSeed.AgentAsync(_fixture, draft.Id)).State);
        await using (var db = _fixture.CreateContext())
        {
            Assert.True((await db.Trades.AsNoTracking().SingleAsync(t => t.Id == trade.Id, Ct)).IsUnmanaged);
            Assert.All(await db.Orders.AsNoTracking().Where(o => o.TradeId == trade.Id).ToListAsync(Ct), o => Assert.Equal(OrderStatus.Canceled, o.Status));
        }

        var nothing = await agents.StopAllAsync(user.Id, closePositions: true, Ct);
        Assert.Equal((0, 0), (nothing.Stopped, nothing.Failed.Count));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task OneLiveAgentPerKeyMarketAndSymbolCanRun()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "pipeline-duplicate@example.com");
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var keyId = await AgentsLiveSeed.AddExchangeKeyAsync(provider.GetRequiredService<IKeyService>(), user.Id, ExchangeEnvironment.Testnet, spot: true, futures: true);
        var first = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "first", AgentState.Running, AgentMode.Live, MarketType.UsdFutures, "BTCUSDT", exchangeKeyId: keyId);
        var second = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "second", AgentState.Draft, AgentMode.Live, MarketType.UsdFutures, "BTCUSDT", exchangeKeyId: keyId);
        // An agent in Error keeps its position and its rows, so it holds its key, market and symbol until it is stopped.
        var errored = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "errored", AgentState.Error, AgentMode.Live, MarketType.UsdFutures, "ETHUSDT", exchangeKeyId: keyId);
        var otherSymbol = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "eth", AgentState.Draft, AgentMode.Live, MarketType.UsdFutures, "ETHUSDT", exchangeKeyId: keyId);
        var otherKey = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "other-key", MarketType.UsdFutures, state: AgentState.Draft);
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude);
        await provider.GetRequiredService<IAccountService>().AcceptDisclaimerAsync(user.Id, Ct);
        var agents = provider.GetRequiredService<IAgentService>();

        var refused = await agents.StartAsync(user.Id, second.Id, Ct);
        var refusedByTheErroredOne = await agents.StartAsync(user.Id, otherSymbol.Id, Ct);
        var allowed = await agents.StartAsync(user.Id, otherKey.Id, Ct);

        Assert.Equal(AgentActionStatus.InvalidConfiguration, refused.Status);
        Assert.Contains("Another live agent already trades BTCUSDT on Futures with this Binance key", refused.Detail);
        Assert.Equal(AgentActionStatus.InvalidConfiguration, refusedByTheErroredOne.Status);
        Assert.Contains("Another live agent already trades ETHUSDT on Futures with this Binance key", refusedByTheErroredOne.Detail);
        Assert.Equal(AgentActionStatus.Succeeded, allowed.Status);
        Assert.Equal(AgentState.Running, (await AgentsLiveSeed.AgentAsync(_fixture, first.Id)).State);
        Assert.Equal(AgentState.Error, (await AgentsLiveSeed.AgentAsync(_fixture, errored.Id)).State);

        await agents.StopAllAsync(user.Id, closePositions: false, Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private EvaluationOutcome? Outcome(Guid agentId, DateTimeOffset candleOpen)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Where(e => e.AgentId == agentId && e.CandleOpenTime == candleOpen && e.FinishedAt != null).Select(e => (EvaluationOutcome?)e.Outcome).SingleOrDefault();
    }

    private decimal? RemainingSize(Guid agentId)
    {
        using var db = _fixture.CreateContext();
        return db.Trades.AsNoTracking().Where(t => t.AgentId == agentId).Select(t => (decimal?)t.RemainingSize).SingleOrDefault();
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
