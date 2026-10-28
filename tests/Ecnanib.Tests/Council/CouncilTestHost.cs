using System.Text.Json;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Configuration;
using Ecnanib.Council;
using Ecnanib.Council.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution;
using Ecnanib.Keys;
using Ecnanib.ModelSync;
using Ecnanib.Strategies;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Council;

/// <summary>
/// Builds the council by hand from fakes (AI, keys, experience, clock) with a context that looks like the pipeline's,
/// and the modules as the application registers them with <c>AddCouncil</c> before <c>AddAgents</c>.
/// </summary>
internal static class CouncilTestHost
{
    public static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>Open time of the evaluated 1H candle; the evaluation starts five seconds after its close.</summary>
    public static readonly DateTimeOffset CandleOpen = new(2026, 1, 5, 11, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset CandleClose = CandleOpen.AddHours(1);

    public static readonly DateTimeOffset Now = CandleClose.AddSeconds(5);

    public static readonly DateTimeOffset Deadline = CandleClose.AddSeconds(180);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ServiceProvider BuildServices(
        string? connectionString = DatabaseTestHost.OfflineConnectionString,
        TestLoggerProvider? logs = null,
        Dictionary<string, string?>? config = null,
        TimeProvider? time = null,
        FakeExchangeClientFactory? exchange = null,
        FakeAiClientFactory? ai = null,
        FakeMarketStreams? streams = null,
        Action<IServiceCollection>? configure = null,
        bool execution = false)
    {
        var values = new Dictionary<string, string?> { ["Database:EncryptionKey"] = DatabaseTestHost.EncryptionKey };
        if (connectionString is not null)
            values["Database:ConnectionString"] = connectionString;
        foreach (var (key, value) in config ?? [])
            values[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddServer(configuration);
        services.AddDatabase(configuration);
        services.AddAccounts(configuration);
        services.AddModelSync(configuration);
        services.AddStrategies();
        services.AddCouncil(configuration);
        if (execution)
            services.AddExecution(configuration);
        services.AddAgents(configuration);
        services.AddKeys(configuration);
        services.AddSingleton<IExchangeClientFactory>(exchange ?? new FakeExchangeClientFactory());
        services.AddSingleton<IAiClientFactory>(ai ?? new FakeAiClientFactory());
        services.AddSingleton<IMarketStreams>(streams ?? new FakeMarketStreams());
        if (logs is not null)
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        if (time is not null)
            services.AddSingleton(time);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static Ecnanib.Council.Services.Council Build(
        FakeAiClientFactory ai,
        FakeKeyResolver? keys = null,
        FakeExperienceSummaries? experience = null,
        CouncilOptions? options = null,
        RiskCapsOptions? caps = null,
        FakeTimeProvider? time = null,
        TestLoggerProvider? logs = null,
        IAgentEvents? events = null)
    {
        var factory = TestLogging.CreateFactory(logs ?? new TestLoggerProvider());
        return new Ecnanib.Council.Services.Council(
            ai,
            keys ?? FakeKeyResolver.WithAllProviders(),
            experience ?? new FakeExperienceSummaries(),
            Options.Create(options ?? new CouncilOptions()),
            Options.Create(caps ?? new RiskCapsOptions()),
            events ?? new FakeAgentEvents(),
            time ?? new FakeTimeProvider(Now),
            factory.CreateLogger<Ecnanib.Council.Services.Council>());
    }

    /// <summary>A Running 1H paper agent on BTCUSDT with the Appendix B council on model "m-{provider}".</summary>
    public static Agent NewAgent(MarketType market = MarketType.Spot, AgentState state = AgentState.Running, bool discretionary = false, Action<Agent>? configure = null)
    {
        var agent = new Agent
        {
            Id = AgentId,
            UserId = UserId,
            Name = "BTC",
            MarketType = market,
            Symbol = "BTCUSDT",
            Timeframe = CandleInterval.OneHour,
            StrategyId = "trend-pullback",
            StrategyVersion = 1,
            Mode = AgentMode.Paper,
            Leverage = market == MarketType.UsdFutures ? 3 : null,
            MarginType = market == MarketType.UsdFutures ? MarginType.Isolated : null,
            Discretionary = discretionary,
            State = state,
            PauseCause = state == AgentState.Paused ? PauseCause.User : null,
            CreatedAt = CandleOpen,
            UpdatedAt = CandleOpen,
        };
        foreach (var role in CouncilRoles.Order)
            agent.CouncilAssignments.Add(new CouncilAssignment { AgentId = agent.Id, Role = role, Provider = CouncilRoles.DefaultProvider(role), Model = Model(CouncilRoles.DefaultProvider(role)), Enabled = true });
        configure?.Invoke(agent);
        return agent;
    }

    public static string Model(AiProvider provider) => "m-" + provider.ToString().ToLowerInvariant();

    public static StrategyDescriptor Descriptor(decimal maxStopDistanceAtr = 3m) => new(
        "trend-pullback", "Trend Pullback", CandleInterval.OneHour, 1, "Pullback to the 1H EMA20/EMA50 in the daily trend.",
        [StrategyParameter.Decimal(StrategyParameters.MaxStopDistanceAtr, "Maximum stop distance (× ATR)", maxStopDistanceAtr, 0.5m, 20m), StrategyParameter.Integer(StrategyParameters.AtrPeriod, "ATR period", 14, 2, 200)],
        [CandleInterval.OneDay], []);

    public static StrategySetup LongSetup(decimal entry = 100m, decimal stop = 96m, params decimal[] targets) =>
        new(PositionSide.Long, OrderType.Limit, entry, stop, targets.Length == 0 ? [108m] : targets, "close below 96", ["noEntryWindows: none (passed)"]);

    public static StrategyOutput Output(StrategySetup? setup, BiasDirection bias = BiasDirection.Long, decimal atr = 2m, decimal close = 100m, string? note = "setup") =>
        new("trend-pullback", 1, bias, [new PriceLevel("emaFast", 99m), new PriceLevel("emaSlow", 97m)],
            new Dictionary<string, decimal> { ["atr"] = atr, ["close"] = close, ["emaFast"] = 99m, ["emaSlow"] = 97m }, setup, note);

    /// <summary>Flat candles closing at <paramref name="close"/>, the last one being the evaluated candle.</summary>
    public static IReadOnlyList<Candle> Candles(int count = 40, decimal close = 100m, CandleInterval interval = CandleInterval.OneHour)
    {
        var span = interval.ToTimeSpan();
        var candles = new List<Candle>();
        for (var i = count - 1; i >= 0; i--)
        {
            var open = CandleOpen - i * span;
            candles.Add(new Candle("BTCUSDT", interval, open, open + span - TimeSpan.FromMilliseconds(1), close - 1, close + 1, close - 2, close, 100m, 100m * close, 10, true));
        }

        return candles;
    }

    public static Trade OpenTrade(Agent agent, PositionSide side = PositionSide.Long, decimal entry = 100m, decimal? stop = 95m, decimal size = 0.5m) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-00000000000a"),
        AgentId = agent.Id,
        EntryDecisionId = Guid.Parse("00000000-0000-0000-0000-00000000000b"),
        Symbol = agent.Symbol,
        MarketType = agent.MarketType,
        Timeframe = agent.Timeframe,
        Side = side,
        EntryPrice = entry,
        Size = size,
        CurrentStop = stop,
        Targets = JsonSerializer.SerializeToElement(new[] { 110m }),
        IsPaper = true,
        OpenedAt = CandleOpen.AddHours(-3),
    };

    public static Order EntryOrder(Agent agent, decimal price = 99m) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-00000000000c"),
        AgentId = agent.Id,
        ClientOrderId = "entry-1",
        Purpose = OrderPurpose.Entry,
        Type = OrderType.Limit,
        Side = OrderSide.Buy,
        Price = price,
        Quantity = 0.5m,
        Status = OrderStatus.New,
        IsPaper = true,
        CreatedAt = CandleOpen,
        UpdatedAt = CandleOpen,
    };

    public static CouncilContext Context(
        Agent? agent = null,
        StrategyOutput? output = null,
        StrategyDescriptor? descriptor = null,
        IReadOnlyList<Trade>? trades = null,
        IReadOnlyList<Order>? orders = null,
        bool? entriesAllowed = null,
        DateTimeOffset? deadline = null,
        MarkPrice? mark = null,
        IReadOnlyList<Candle>? candles = null)
    {
        agent ??= NewAgent();
        descriptor ??= Descriptor();
        output ??= Output(LongSetup());
        var evaluation = new Evaluation
        {
            Id = Guid.Parse("00000000-0000-0000-0000-00000000000e"),
            AgentId = agent.Id,
            Symbol = agent.Symbol,
            MarketType = agent.MarketType,
            Timeframe = agent.Timeframe,
            CandleOpenTime = CandleOpen,
            StartedAt = Now,
            Outcome = EvaluationOutcome.InProgress,
        };
        var own = candles ?? Candles();
        var higher = new Dictionary<CandleInterval, IReadOnlyList<Candle>> { [CandleInterval.OneDay] = Candles(12, 100m, CandleInterval.OneDay) };
        var market = new MarketSnapshot(own, higher, new Dictionary<ReferenceSeries, IReadOnlyList<Candle>>(), mark);
        return new CouncilContext(agent, evaluation, descriptor, descriptor.Resolve(new Dictionary<string, JsonElement>()), output, market,
            trades ?? [], orders ?? [], entriesAllowed ?? agent.State == AgentState.Running, deadline ?? Deadline);
    }

    /// <summary>A non-final role's answer as JSON (Appendix C role output).</summary>
    public static string RoleJson(string action = "no_trade", string entryType = "none", decimal? entry = null, decimal? stop = null, decimal[]? targets = null,
        decimal confidence = 0.6m, string assessment = "Looks fine.", string[]? flags = null)
    {
        var document = new Dictionary<string, object?>
        {
            ["assessment"] = assessment, ["confidence"] = confidence, ["proposed_action"] = action, ["entry_type"] = entryType,
            ["entry_price"] = entry, ["stop_price"] = stop, ["targets"] = targets ?? [], ["flags"] = flags ?? [],
        };
        return JsonSerializer.Serialize(document);
    }

    /// <summary>The Risk Manager's answer as JSON (Appendix C final decision).</summary>
    public static string FinalJson(string action = "no_trade", string entryType = "none", decimal? entry = null, decimal? stop = null, decimal[]? targets = null,
        decimal confidence = 0.7m, string assessment = "Checked.", string[]? flags = null, decimal? risk = null, string rationale = "Because.", decimal? partial = null)
    {
        var document = new Dictionary<string, object?>
        {
            ["assessment"] = assessment, ["confidence"] = confidence, ["proposed_action"] = action, ["entry_type"] = entryType,
            ["entry_price"] = entry, ["stop_price"] = stop, ["targets"] = targets ?? [], ["flags"] = flags ?? [],
            ["risk_percent"] = risk, ["rationale"] = rationale, ["take_partial_fraction"] = partial,
        };
        return JsonSerializer.Serialize(document);
    }

    /// <summary>Answers every role with no_trade and the Risk Manager with <paramref name="final"/>; a role in <paramref name="overrides"/> gets its own text.</summary>
    public static FakeAiClientFactory Answering(string? final = null, IReadOnlyDictionary<CouncilRole, string>? overrides = null)
    {
        var ai = new FakeAiClientFactory();
        ai.OnComplete = (key, request, _) =>
        {
            var role = Enum.Parse<CouncilRole>(request.Purpose!);
            var text = overrides is not null && overrides.TryGetValue(role, out var scripted) ? scripted
                : role == CouncilRole.RiskManager ? final ?? FinalJson()
                : RoleJson();
            return Task.FromResult(FakeAiClientFactory.Completion(key.Provider, request.Model, text));
        };
        return ai;
    }
}
