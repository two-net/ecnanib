using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Candle = Ecnanib.Exchange.Abstractions.Candle;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>Users, agents, decisions and trades for the agents live tests; keys go through the keys service with faked validators.</summary>
internal static class AgentsLiveSeed
{
    public const string ExchangeApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";

    public const string ExchangeSecret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";

    public const string ProviderApiKey = "sk-test-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789wxyz";

    public static readonly DateTimeOffset Now = new(2026, 1, 5, 0, 0, 5, TimeSpan.Zero);

    public static readonly MarketKey SpotBtc = new(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<User> NewUserAsync(DatabaseLiveFixture fixture, string email)
    {
        await using var db = fixture.CreateContext();
        var user = LiveSeed.NewUser(email);
        db.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    public static async Task<Agent> NewAgentAsync(
        DatabaseLiveFixture fixture, Guid userId, string name, AgentState state = AgentState.Draft, AgentMode mode = AgentMode.Paper,
        MarketType market = MarketType.Spot, string symbol = "BTCUSDT", CandleInterval timeframe = CandleInterval.OneHour, string strategyId = "trend-pullback",
        Guid? exchangeKeyId = null, string? model = "test-model", bool discretionary = false, int strategyVersion = 1)
    {
        await using var db = fixture.CreateContext();
        var agent = new Agent
        {
            UserId = userId,
            Name = name,
            MarketType = market,
            Symbol = symbol,
            Timeframe = timeframe,
            StrategyId = strategyId,
            StrategyVersion = strategyVersion,
            Mode = mode,
            ExchangeKeyId = exchangeKeyId,
            Leverage = market == MarketType.UsdFutures ? 3 : null,
            MarginType = market == MarketType.UsdFutures ? MarginType.Isolated : null,
            Discretionary = discretionary,
            State = state,
            PauseCause = state == AgentState.Paused ? PauseCause.User : null,
            StateReason = state == AgentState.Paused ? AgentService.PausedByUser : null,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        foreach (var role in AgentRules.Roles)
            agent.CouncilAssignments.Add(new CouncilAssignment { Role = role, Provider = AgentRules.DefaultProvider(role), Model = model ?? "", Enabled = true });
        db.Add(agent);
        await db.SaveChangesAsync(Ct);
        return agent;
    }

    public static async Task<Decision> NewDecisionAsync(DatabaseLiveFixture fixture, Agent agent, DateTimeOffset candleOpen, decimal? stop = 90m, decimal? target = 120m)
    {
        await using var db = fixture.CreateContext();
        var evaluation = new Evaluation
        {
            AgentId = agent.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe, CandleOpenTime = candleOpen,
            StartedAt = candleOpen.AddHours(1), FinishedAt = candleOpen.AddHours(1).AddSeconds(5), CouncilConsulted = true, Outcome = EvaluationOutcome.Decided,
        };
        var decision = new Decision
        {
            Evaluation = evaluation, FinalAction = DecisionAction.OpenLong, EntryType = OrderType.Market, StopPrice = stop,
            Targets = JsonSerializer.SerializeToElement(target is { } t ? new[] { t } : Array.Empty<decimal>()), Size = 0.1m, Rationale = "seeded", CreatedAt = candleOpen.AddHours(1),
        };
        db.AddRange(evaluation, decision);
        await db.SaveChangesAsync(Ct);
        return decision;
    }

    /// <summary>An open paper trade with its stop-loss (and target) orders, as the simulator would have created them.</summary>
    public static async Task<Trade> NewOpenTradeAsync(DatabaseLiveFixture fixture, Agent agent, Decision decision, decimal entry = 100m, decimal? stop = 90m, decimal? target = 120m)
    {
        await using var db = fixture.CreateContext();
        var trade = new Trade
        {
            AgentId = agent.Id, EntryDecisionId = decision.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe,
            Side = PositionSide.Long, EntryPrice = entry, Size = 0.1m, CurrentStop = stop,
            StopHistory = stop is { } s ? JsonSerializer.SerializeToElement(new[] { new { at = Now, price = s, reason = "initial" } }) : JsonValues.EmptyArray,
            Targets = JsonSerializer.SerializeToElement(target is { } t ? new[] { t } : Array.Empty<decimal>()), IsPaper = true, OpenedAt = Now,
        };
        db.Add(trade);
        await db.SaveChangesAsync(Ct);
        if (stop is { } stopPrice)
        {
            db.Add(new Order
            {
                AgentId = agent.Id, DecisionId = decision.Id, TradeId = trade.Id, ClientOrderId = "seed-sl-" + Guid.NewGuid().ToString("N")[..20], Purpose = OrderPurpose.StopLoss,
                Type = OrderType.StopMarket, Side = OrderSide.Sell, StopPrice = stopPrice, Quantity = trade.Size, Status = OrderStatus.New, ReduceOnly = true, IsPaper = true, CreatedAt = Now, UpdatedAt = Now,
            });
        }

        if (target is { } targetPrice)
        {
            db.Add(new Order
            {
                AgentId = agent.Id, DecisionId = decision.Id, TradeId = trade.Id, ClientOrderId = "seed-tp-" + Guid.NewGuid().ToString("N")[..20], Purpose = OrderPurpose.TakeProfit,
                Type = OrderType.Limit, Side = OrderSide.Sell, Price = targetPrice, Quantity = trade.Size, Status = OrderStatus.New, ReduceOnly = true, IsPaper = true, CreatedAt = Now, UpdatedAt = Now,
            });
        }

        await db.SaveChangesAsync(Ct);
        return trade;
    }

    public static async Task<Agent> AgentAsync(DatabaseLiveFixture fixture, Guid agentId)
    {
        await using var db = fixture.CreateContext();
        return await db.Agents.AsNoTracking().Include(a => a.CouncilAssignments).Include(a => a.EntryWindows).AsSplitQuery().SingleAsync(a => a.Id == agentId, Ct);
    }

    /// <summary>Wraps the registered <see cref="IStrategyCatalog"/> (whatever registered it) so a test can present other versions.</summary>
    public static void WithCatalog(IServiceCollection services, Func<IStrategyCatalog, IStrategyCatalog> decorate)
    {
        var original = services.Last(d => d.ServiceType == typeof(IStrategyCatalog));
        services.Remove(original);
        services.AddSingleton(sp =>
        {
            var inner = original.ImplementationInstance as IStrategyCatalog
                ?? original.ImplementationFactory?.Invoke(sp) as IStrategyCatalog
                ?? (IStrategyCatalog)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return decorate(inner);
        });
    }

    public static async Task<List<AgentChange>> ChangesAsync(DatabaseLiveFixture fixture, Guid agentId)
    {
        await using var db = fixture.CreateContext();
        return await db.AgentChanges.AsNoTracking().Where(c => c.AgentId == agentId).OrderBy(c => c.Id).ToListAsync(Ct);
    }

    public static async Task SetStateAsync(DatabaseLiveFixture fixture, Guid agentId, AgentState state)
    {
        await using var db = fixture.CreateContext();
        await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, state), Ct);
    }

    public static async Task DeleteUsersAsync(DatabaseLiveFixture fixture, params Guid[] userIds)
    {
        await using var db = fixture.CreateContext();
        await db.Users.Where(u => userIds.Contains(u.Id)).ExecuteDeleteAsync(Ct);
    }

    public static async Task AddProviderKeysAsync(IKeyService keys, Guid userId, params AiProvider[] providers)
    {
        foreach (var provider in providers)
        {
            var result = await keys.SetProviderKeyAsync(userId, provider, ProviderApiKey, Ct);
            Assert.Equal(KeySaveStatus.Succeeded, result.Status);
        }
    }

    public static async Task<Guid> AddExchangeKeyAsync(IKeyService keys, Guid userId, ExchangeEnvironment environment, bool spot, bool futures)
    {
        var result = await keys.AddExchangeKeyAsync(userId, new ExchangeKeyInput("key", environment, spot, futures, ExchangeApiKey, ExchangeSecret), Ct);
        Assert.Equal(KeySaveStatus.Succeeded, result.Status);
        return result.KeyId!.Value;
    }

    /// <summary>The modules the agents module needs, against the fixture's database, with the exchange listing BTCUSDT and ETHUSDT on every market.</summary>
    public static (ServiceProvider Provider, TestLoggerProvider Logs, FakeTimeProvider Time, FakeExchangeClientFactory Exchange, FakeMarketStreams Streams) Build(
        DatabaseLiveFixture fixture, Dictionary<string, string?>? config = null, Action<IServiceCollection>? configure = null, DateTimeOffset? now = null)
    {
        var logs = new TestLoggerProvider();
        var time = new FakeTimeProvider(now ?? Now);
        var exchange = new FakeExchangeClientFactory
        {
            OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT", "ETHUSDT"),
            OnRecentCandles = (symbol, interval, count) => RecentCandles(symbol, interval, count, time.GetUtcNow()),
        };
        var streams = new FakeMarketStreams();
        var provider = AgentsTestHost.BuildServices(fixture.ConnectionString, logs, config, time, exchange, new FakeAiClientFactory(), streams, configure);
        return (provider, logs, time, exchange, streams);
    }

    /// <summary>Three closed candles up to the last boundary before <paramref name="now"/>.</summary>
    public static IReadOnlyList<Candle> RecentCandles(string symbol, CandleInterval interval, int count, DateTimeOffset now)
    {
        var lastOpen = interval.OpenTimeOf(now) - interval.ToTimeSpan();
        return Enumerable.Range(0, Math.Min(count, 3)).Reverse()
            .Select(i => FakeMarketStreams.ClosedCandle(symbol, interval, lastOpen - i * interval.ToTimeSpan(), 100m))
            .ToList();
    }
}
