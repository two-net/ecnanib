using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Logging;
using Candle = Ecnanib.Database.Entities.Candle;

namespace Ecnanib.Tests.Database.Live;

/// <summary>Builds one user with one of everything, so round-trip, cascade and retention tests share a graph.</summary>
internal static class LiveSeed
{
    public static readonly DateTimeOffset Now = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static User NewUser(string email = "trader@example.com") => new()
    {
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        PasswordHash = "$argon2id$hash",
        SecurityStamp = Guid.NewGuid().ToString("N"),
        CreatedAt = Now,
    };

    public static Agent NewAgent(User user, string name = "BTC 1H") => new()
    {
        User = user,
        Name = name,
        MarketType = MarketType.UsdFutures,
        Symbol = "BTCUSDT",
        Timeframe = CandleInterval.OneHour,
        StrategyId = "trend-pullback",
        StrategyVersion = 1,
        Parameters = Json("""{"atrPeriod":14,"emaFast":20}"""),
        Mode = AgentMode.Paper,
        Leverage = 3,
        MarginType = MarginType.Isolated,
        RiskLimits = new RiskLimits { RiskPerTradePercent = 0.5m, MaxOpenPositions = 1, DailyLossLimitPercent = 2m, MaxNotional = 10_000m },
        State = AgentState.Draft,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    public static Evaluation NewEvaluation(Agent agent, DateTimeOffset candleOpenTime) => new()
    {
        Agent = agent,
        Symbol = agent.Symbol,
        MarketType = agent.MarketType,
        Timeframe = agent.Timeframe,
        CandleOpenTime = candleOpenTime,
        StartedAt = candleOpenTime.AddHours(1),
        FinishedAt = candleOpenTime.AddHours(1).AddSeconds(20),
        StrategyOutput = Json("""{"bias":"long","setup":true}"""),
        CouncilConsulted = true,
        Outcome = EvaluationOutcome.Decided,
    };

    public static Decision NewDecision(Evaluation evaluation, DateTimeOffset createdAt) => new()
    {
        Evaluation = evaluation,
        FinalAction = DecisionAction.OpenLong,
        EntryType = OrderType.Limit,
        EntryPrice = 50_000m,
        StopPrice = 49_000m,
        Targets = Json("[52000, 53000]"),
        Size = 0.01m,
        RiskPercent = 0.5m,
        Confidence = 0.7m,
        Rationale = "Pullback to EMA20 with daily bias long.",
        LimitsCheck = Json("""{"riskPerTrade":"pass","leverage":"pass"}"""),
        TotalInputTokens = 1200,
        TotalOutputTokens = 300,
        TotalLatencyMs = 4200,
        CreatedAt = createdAt,
        RoleOutputs =
        [
            new DecisionRoleOutput { Role = CouncilRole.MarketAnalyst, Sequence = 1, Provider = AiProvider.Gpt, Model = "gpt-test", Status = RoleOutputStatus.Succeeded, ProposedAction = DecisionAction.OpenLong, Confidence = 0.6m, InputTokens = 600, OutputTokens = 150, LatencyMs = 2000 },
            new DecisionRoleOutput { Role = CouncilRole.RiskManager, Sequence = 4, Provider = AiProvider.Claude, Model = "claude-test", Status = RoleOutputStatus.Succeeded, ProposedAction = DecisionAction.OpenLong, Confidence = 0.7m, InputTokens = 600, OutputTokens = 150, LatencyMs = 2200 },
        ],
        Transcripts =
        [
            new DeliberationTranscript { Role = CouncilRole.MarketAnalyst, InputSummary = "candles, levels", Output = Json("""{"assessment":"constructive"}"""), CreatedAt = createdAt },
            new DeliberationTranscript { Role = CouncilRole.RiskManager, InputSummary = "analyst output, limits", Output = Json("""{"action":"open_long"}"""), CreatedAt = createdAt },
        ],
        ShadowOutcome = new ShadowOutcome { Result = ShadowResult.Pending },
    };

    public static Candle NewCandle(DateTimeOffset openTime) => new()
    {
        Environment = ExchangeEnvironment.Production,
        MarketType = MarketType.UsdFutures,
        Symbol = "BTCUSDT",
        Interval = CandleInterval.OneHour,
        OpenTime = openTime,
        CloseTime = openTime.AddHours(1),
        Open = 50_000m,
        High = 50_500m,
        Low = 49_800m,
        Close = 50_200m,
        Volume = 12.5m,
        QuoteVolume = 626_000m,
        TradeCount = 1500,
    };

    public static LogEntry NewLogEntry(DateTimeOffset timestamp, Guid? userId = null, Guid? agentId = null) => new()
    {
        Timestamp = timestamp,
        Level = LogLevel.Information,
        Component = "Ecnanib.Tests",
        Message = "seeded",
        UserId = userId,
        AgentId = agentId,
        Context = Json("""{"seed":true}"""),
    };

    /// <summary>Adds the whole graph to the context and saves it, returning the user and agent.</summary>
    public static async Task<(User User, Agent Agent, Decision Decision, Trade Trade)> SeedEverythingAsync(EcnanibDbContext db, CancellationToken ct)
    {
        var user = NewUser();
        var exchangeKey = new ExchangeKey
        {
            User = user, Label = "testnet", Environment = ExchangeEnvironment.Testnet, AllowSpot = true, AllowFutures = true,
            ApiKey = "abcdefgh12345", ApiSecret = "s3cr3tvalue", PermissionsSnapshot = Json("""{"enableWithdrawals":false}"""),
            Status = KeyStatus.Valid, LastValidatedAt = Now, CreatedAt = Now,
        };
        var providerKey = new ProviderKey { User = user, Provider = AiProvider.Claude, ApiKey = "sk-ant-test", Status = KeyStatus.Valid, LastValidatedAt = Now, CreatedAt = Now };
        var agent = NewAgent(user);
        agent.ExchangeKey = exchangeKey;
        agent.Mode = AgentMode.Live;
        agent.CouncilAssignments.Add(new CouncilAssignment { Role = CouncilRole.RiskManager, Provider = AiProvider.Claude, Model = "claude-test", Enabled = true });
        agent.CouncilAssignments.Add(new CouncilAssignment { Role = CouncilRole.SentimentFlow, Provider = AiProvider.Grok, Model = "grok-test", Enabled = false });
        agent.EntryWindows.Add(new EntryWindow { Kind = EntryWindowKind.OneOff, StartAt = Now.AddDays(1), EndAt = Now.AddDays(2) });
        agent.EntryWindows.Add(new EntryWindow { Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Saturday, StartTime = new TimeOnly(0, 0), EndDay = DayOfWeek.Sunday, EndTime = new TimeOnly(23, 59) });
        var change = new AgentChange { Agent = agent, Timestamp = Now, Field = "Name", OldValue = Json("\"old\""), NewValue = Json("\"BTC 1H\"") };
        var evaluation = NewEvaluation(agent, Now.AddHours(-2));
        var decision = NewDecision(evaluation, Now.AddHours(-1));
        var order = new Order
        {
            Agent = agent, Decision = decision, ClientOrderId = "ecn-" + Guid.NewGuid().ToString("N")[..20], ExchangeOrderId = 123456789,
            Purpose = OrderPurpose.Entry, Type = OrderType.Limit, Side = OrderSide.Buy, Price = 50_000m, Quantity = 0.01m, FilledQuantity = 0.01m,
            AverageFillPrice = 50_000m, Status = OrderStatus.Filled, IsPaper = false, ExpiresAt = Now.AddHours(1), CreatedAt = Now, UpdatedAt = Now, FilledAt = Now,
        };
        var candle = NewCandle(Now.AddHours(-2));
        var model = new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-test", DisplayName = "Claude Test", ProviderCreatedAt = Now.AddDays(-30), InputTokenLimit = 200_000, OutputTokenLimit = 8192, SupportsTextGeneration = true, Capabilities = Json("""{"vision":true}"""), FirstSeenAt = Now, LastSeenAt = Now };
        var sync = new ProviderSyncState { Provider = AiProvider.Claude, LastSyncAt = Now, LastStatus = SyncStatus.Succeeded, ModelCount = 1, Trigger = SyncTrigger.KeyValidation };
        var systemKey = new SystemKey { Purpose = "test", Value = "value", CreatedAt = Now };
        var disclaimer = new DisclaimerAcceptance { User = user, Version = "2026-01", AcceptedAt = Now };
        var agentStat = new ExperienceStat { Scope = StatScope.Agent, Agent = agent, User = user, Provider = AiProvider.Claude, Model = "claude-test", Role = CouncilRole.RiskManager, Timeframe = CandleInterval.OneHour, MarketType = MarketType.UsdFutures, Metrics = new ExperienceMetrics { Decisions = 1, Approvals = 1, WinRate = 1m }, ComputedAt = Now };
        var systemStat = new ExperienceStat { Scope = StatScope.System, Provider = AiProvider.Claude, Model = "claude-test", Role = CouncilRole.RiskManager, Timeframe = CandleInterval.OneHour, MarketType = MarketType.UsdFutures, Metrics = new ExperienceMetrics { Decisions = 1 }, ComputedAt = Now };

        db.AddRange(user, exchangeKey, providerKey, agent, change, evaluation, decision, order, candle, model, sync, systemKey, disclaimer, agentStat, systemStat);
        await db.SaveChangesAsync(ct);

        // Trade and decision reference each other, so the trade is saved after its entry decision.
        var trade = new Trade
        {
            Agent = agent, EntryDecision = decision, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe,
            Side = PositionSide.Long, EntryPrice = 50_000m, Size = 0.01m, CurrentStop = 49_000m,
            StopHistory = Json("""[{"at":"2026-01-05T00:00:00Z","price":49000,"reason":"initial"}]"""), Targets = Json("[52000]"),
            Fees = 0.5m, Funding = 0.1m, IsPaper = false, OpenedAt = Now,
        };
        db.Add(trade);
        await db.SaveChangesAsync(ct);
        order.Trade = trade;
        db.Add(NewLogEntry(Now, user.Id, agent.Id));
        await db.SaveChangesAsync(ct);

        return (user, agent, decision, trade);
    }
}
