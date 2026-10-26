using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>Agents, decisions, requests and live keys for the execution live tests, on top of the agents seed with the execution module registered.</summary>
internal static class ExecutionLiveSeed
{
    /// <summary>The 1H candle that closed five seconds before <see cref="AgentsLiveSeed.Now"/>.</summary>
    public static readonly DateTimeOffset LastOpen = new(2026, 1, 4, 23, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset LastClose = LastOpen.AddHours(1);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static (ServiceProvider Provider, TestLoggerProvider Logs, FakeTimeProvider Time, FakeExchangeClientFactory Exchange, FakeMarketStreams Streams) Build(
        DatabaseLiveFixture fixture, Dictionary<string, string?>? config = null, Action<IServiceCollection>? configure = null, DateTimeOffset? now = null, FakeExchangeClientFactory? exchange = null)
    {
        var values = new Dictionary<string, string?> { ["Execution:FillTrackerStartDelaySeconds"] = "0", ["Execution:ProtectiveOrderRetryDelayMs"] = "100" };
        foreach (var (key, value) in config ?? [])
            values[key] = value;
        var built = AgentsLiveSeed.Build(fixture, values, configure, now, marketData: false, execution: true, exchange);
        built.Exchange.OnPrice = _ => built.Exchange.Account("none", MarketType.Spot).MarketPrice;
        return built;
    }

    /// <summary>A live agent on a fresh Binance key of the user, validated through the fake exchange.</summary>
    public static async Task<Agent> LiveAgentAsync(DatabaseLiveFixture fixture, ServiceProvider provider, Guid userId, string name, MarketType market, string symbol = "BTCUSDT",
        ExchangeEnvironment environment = ExchangeEnvironment.Testnet, AgentState state = AgentState.Running, int leverage = 3, bool discretionary = false)
    {
        // One key per agent, so each agent has its own scripted account book.
        var result = await provider.GetRequiredService<IKeyService>().AddExchangeKeyAsync(userId,
            new ExchangeKeyInput($"{name}-{Guid.NewGuid():N}"[..20], environment, true, true, AgentsLiveSeed.ExchangeApiKey, AgentsLiveSeed.ExchangeSecret), Ct);
        Assert.Equal(KeySaveStatus.Succeeded, result.Status);
        var keyId = result.KeyId!.Value;
        var agent = await AgentsLiveSeed.NewAgentAsync(fixture, userId, name, state, AgentMode.Live, market, symbol, exchangeKeyId: keyId, discretionary: discretionary);
        if (market == MarketType.UsdFutures && leverage != 3)
        {
            await using var db = fixture.CreateContext();
            await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Leverage, leverage), Ct);
            agent.Leverage = leverage;
        }

        return agent;
    }

    public static FakeExchangeAccount Account(FakeExchangeClientFactory exchange, Agent agent) =>
        exchange.Account(agent.ExchangeKeyId!.Value.ToString("D"), agent.MarketType);

    public static async Task SetParametersAsync(DatabaseLiveFixture fixture, Agent agent, Dictionary<string, JsonElement> parameters)
    {
        var json = StrategyParameters.Write(parameters);
        await using var db = fixture.CreateContext();
        await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Parameters, json), Ct);
        agent.Parameters = json;
    }

    /// <summary>The agent's open-position cap (an owned type, so loaded and saved rather than updated in place).</summary>
    public static async Task SetMaxOpenPositionsAsync(DatabaseLiveFixture fixture, Agent agent, int maxOpenPositions)
    {
        await using var db = fixture.CreateContext();
        var row = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
        row.RiskLimits = new RiskLimits { RiskPerTradePercent = row.RiskLimits.RiskPerTradePercent, MaxOpenPositions = maxOpenPositions, DailyLossLimitPercent = row.RiskLimits.DailyLossLimitPercent, MaxNotional = row.RiskLimits.MaxNotional };
        await db.SaveChangesAsync(Ct);
        agent.RiskLimits = row.RiskLimits;
    }

    public static async Task<Evaluation> NewEvaluationAsync(DatabaseLiveFixture fixture, Agent agent, DateTimeOffset candleOpen)
    {
        await using var db = fixture.CreateContext();
        var evaluation = new Evaluation
        {
            AgentId = agent.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe, CandleOpenTime = candleOpen,
            StartedAt = candleOpen.AddHours(1), Outcome = EvaluationOutcome.InProgress,
        };
        db.Add(evaluation);
        await db.SaveChangesAsync(Ct);
        return evaluation;
    }

    public static async Task<Decision> NewDecisionAsync(DatabaseLiveFixture fixture, Agent agent, DateTimeOffset candleOpen, DecisionAction action = DecisionAction.OpenLong,
        OrderType? entryType = OrderType.Limit, decimal? entry = 100m, decimal? stop = 95m, decimal[]? targets = null, decimal? risk = null, decimal? partial = null, Guid? tradeId = null, string rationale = "seeded")
    {
        await using var db = fixture.CreateContext();
        var evaluation = new Evaluation
        {
            AgentId = agent.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe, CandleOpenTime = candleOpen,
            StartedAt = candleOpen.AddHours(1), FinishedAt = candleOpen.AddHours(1).AddSeconds(5), CouncilConsulted = true, Outcome = EvaluationOutcome.Decided,
        };
        var decision = new Decision
        {
            Evaluation = evaluation, FinalAction = action, EntryType = entryType, EntryPrice = entryType == OrderType.Limit ? entry : null, StopPrice = stop,
            Targets = JsonSerializer.SerializeToElement(targets ?? [110m, 130m]), RiskPercent = risk, PartialFraction = partial, TradeId = tradeId, Rationale = rationale, CreatedAt = candleOpen.AddHours(1),
        };
        db.AddRange(evaluation, decision);
        await db.SaveChangesAsync(Ct);
        return decision;
    }

    public static async Task<Decision> DecisionAsync(DatabaseLiveFixture fixture, Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Decisions.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
    }

    public static async Task<(List<Trade> Trades, List<Order> Orders)> OpenAsync(DatabaseLiveFixture fixture, Guid agentId)
    {
        await using var db = fixture.CreateContext();
        var trades = await db.Trades.AsNoTracking().Where(t => t.AgentId == agentId && t.ClosedAt == null).OrderBy(t => t.OpenedAt).ToListAsync(Ct);
        var orders = await db.Orders.AsNoTracking().Where(o => o.AgentId == agentId && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled)).OrderBy(o => o.CreatedAt).ToListAsync(Ct);
        return (trades, orders);
    }

    public static async Task<List<Order>> OrdersOfAsync(DatabaseLiveFixture fixture, Guid agentId)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.AsNoTracking().Where(o => o.AgentId == agentId).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync(Ct);
    }

    public static async Task<Trade?> TradeOfAsync(DatabaseLiveFixture fixture, Guid agentId)
    {
        await using var db = fixture.CreateContext();
        return await db.Trades.AsNoTracking().Where(t => t.AgentId == agentId).OrderBy(t => t.OpenedAt).FirstOrDefaultAsync(Ct);
    }

    public static async Task<Agent> AgentAsync(DatabaseLiveFixture fixture, Guid agentId) => await AgentsLiveSeed.AgentAsync(fixture, agentId);

    /// <summary>An execution request for the evaluated candle, with the open state read fresh and the engine output built around <paramref name="setup"/>.</summary>
    public static async Task<ExecutionRequest> RequestAsync(DatabaseLiveFixture fixture, ServiceProvider provider, Agent agent, Evaluation evaluation, decimal close = 100m, StrategySetup? setup = null,
        bool entriesAllowed = true, IReadOnlyList<PriceLevel>? levels = null, decimal atr = 2m, Dictionary<string, JsonElement>? overrides = null)
    {
        var catalog = provider.GetRequiredService<IStrategyCatalog>();
        var strategy = catalog.Find(agent.StrategyId, agent.StrategyVersion)!;
        var stored = new Dictionary<string, JsonElement>(StrategyParameters.Read(agent.Parameters));
        foreach (var (key, value) in overrides ?? [])
            stored[key] = value;
        var parameters = strategy.Resolve(stored);
        var output = new StrategyOutput(strategy.Id, strategy.Version, BiasDirection.Long, levels ?? [new PriceLevel("emaFast", 99m)], new Dictionary<string, decimal> { ["atr"] = atr, ["close"] = close }, setup, "seeded");
        var market = new MarketSnapshot(Candles(agent.Symbol, close), new Dictionary<CandleInterval, IReadOnlyList<Candle>>(), new Dictionary<ReferenceSeries, IReadOnlyList<Candle>>(), null);
        var (trades, orders) = await OpenAsync(fixture, agent.Id);
        ExchangeKeyHandle? handle = null;
        if (agent.Mode == AgentMode.Live)
        {
            var resolution = await provider.GetRequiredService<IKeyResolver>().ResolveExchangeKeyAsync(agent.UserId, agent.ExchangeKeyId!.Value, agent.MarketType, Ct);
            handle = resolution.Handle;
        }

        var venue = provider.GetRequiredService<IOrderVenues>().For(agent.Mode);
        var caps = provider.GetRequiredService<IOptions<RiskCapsOptions>>().Value;
        return new ExecutionRequest(agent, evaluation, strategy, parameters, output, market, trades, orders, caps, venue, handle, entriesAllowed, LastClose.AddMinutes(3));
    }

    /// <summary>Flat 1H candles closing at <paramref name="close"/>, the last one opening at <see cref="LastOpen"/>.</summary>
    public static IReadOnlyList<Candle> Candles(string symbol, decimal close, int count = 5)
    {
        var span = TimeSpan.FromHours(1);
        var candles = new List<Candle>();
        for (var i = count - 1; i >= 0; i--)
        {
            var open = LastOpen - i * span;
            candles.Add(new Candle(symbol, CandleInterval.OneHour, open, open + span - TimeSpan.FromMilliseconds(1), close - 1, close + 1, close - 2, close, 100m, 100m * close, 10, true));
        }

        return candles;
    }

    public static Dictionary<string, JsonElement> Parameters(bool? marketFallback = null, decimal? partialFraction = null, int? entryTimeout = null, decimal? stopBuffer = null)
    {
        var values = new Dictionary<string, JsonElement>();
        if (marketFallback is { } fallback)
            values[StrategyParameters.MarketFallback] = JsonSerializer.SerializeToElement(fallback);
        if (partialFraction is { } fraction)
            values[StrategyParameters.PartialTakeProfitFraction] = JsonSerializer.SerializeToElement(fraction);
        if (entryTimeout is { } timeout)
            values[StrategyParameters.EntryTimeoutCandles] = JsonSerializer.SerializeToElement(timeout);
        if (stopBuffer is { } buffer)
            values[StrategyParameters.StopBufferAtr] = JsonSerializer.SerializeToElement(buffer);
        return values;
    }

    public static Dictionary<string, string?> Checks(Decision decision) =>
        decision.ExecutionCheck is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().ToDictionary(c => c.GetProperty("limit").GetString()!, c => c.GetProperty("status").GetString())
            : [];
}
