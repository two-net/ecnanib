using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Experience.Live;

/// <summary>One role's turn in a seeded decision.</summary>
internal sealed record SeedTurn(CouncilRole Role, AiProvider Provider, string Model, RoleOutputStatus Status = RoleOutputStatus.Succeeded, DecisionAction? Proposed = null, int LatencyMs = 1000);

/// <summary>Decisions with their turns, closed trades and shadow outcomes for the experience live tests.</summary>
internal static class ExperienceLiveSeed
{
    public const string LimitsAllPass = """[{"limit":"riskPerTrade","status":"pass","detail":"ok"},{"limit":"leverage","status":"pass","detail":"ok"}]""";

    public const string LimitsClamped = """[{"limit":"riskPerTrade","status":"clamped","detail":"5% reduced to the limit of 1%"},{"limit":"leverage","status":"pass","detail":"ok"}]""";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static SeedTurn Turn(CouncilRole role, AiProvider provider, string model, DecisionAction? proposed, RoleOutputStatus status = RoleOutputStatus.Succeeded, int latencyMs = 1000) =>
        new(role, provider, model, status, proposed, latencyMs);

    /// <summary>An evaluation and its decision with the given turns; a limits check only when given (the Risk Manager failed otherwise).</summary>
    public static async Task<Decision> NewDecisionAsync(DatabaseLiveFixture fixture, Agent agent, DateTimeOffset candleOpen, DecisionAction final, IReadOnlyList<SeedTurn> turns,
        string? limits = LimitsAllPass, decimal? stop = 90m, decimal? target = 120m, Guid? tradeId = null)
    {
        await using var db = fixture.CreateContext();
        var evaluation = new Evaluation
        {
            AgentId = agent.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe, CandleOpenTime = candleOpen,
            StartedAt = candleOpen.AddHours(1), FinishedAt = candleOpen.AddHours(1).AddSeconds(5), CouncilConsulted = true,
            Outcome = limits is null ? EvaluationOutcome.Failed : EvaluationOutcome.Decided,
        };
        var decision = new Decision
        {
            Evaluation = evaluation, FinalAction = final, EntryType = final is DecisionAction.OpenLong or DecisionAction.OpenShort ? OrderType.Limit : null,
            EntryPrice = 100m, StopPrice = stop, Targets = JsonSerializer.SerializeToElement(target is { } t ? new[] { t } : Array.Empty<decimal>()),
            Rationale = "seeded", LimitsCheck = limits is null ? null : JsonDocument.Parse(limits).RootElement.Clone(), CreatedAt = candleOpen.AddHours(1), TradeId = tradeId,
            TotalLatencyMs = turns.Sum(t => t.LatencyMs),
        };
        foreach (var turn in turns)
        {
            // A role that was disabled or skipped made no call and has no tokens; the decision's totals are the sum of its rows.
            var called = turn.Status is not (RoleOutputStatus.Disabled or RoleOutputStatus.Skipped);
            decision.RoleOutputs.Add(new DecisionRoleOutput
            {
                Role = turn.Role, Sequence = Ecnanib.Agents.Abstractions.CouncilRoles.Sequence(turn.Role), Provider = turn.Provider, Model = turn.Model, Status = turn.Status,
                ProposedAction = turn.Status == RoleOutputStatus.Succeeded ? turn.Proposed : null, Confidence = turn.Status == RoleOutputStatus.Succeeded ? 0.6m : null,
                InputTokens = called ? 10 : 0, OutputTokens = called ? 5 : 0, Attempts = called ? 1 : 0, LatencyMs = called ? turn.LatencyMs : 0,
            });
            if (called)
            {
                decision.TotalInputTokens += 10;
                decision.TotalOutputTokens += 5;
            }
        }

        db.AddRange(evaluation, decision);
        await db.SaveChangesAsync(Ct);
        return decision;
    }

    /// <summary>A closed paper trade opened by the decision: R against the initial stop, P&amp;L net of fees, the stop history as the simulator writes it.</summary>
    public static async Task<Trade> NewClosedTradeAsync(DatabaseLiveFixture fixture, Agent agent, Decision decision, decimal entry, decimal stop, decimal exit, decimal size, decimal fees,
        DateTimeOffset closedAt, TradeExitReason reason = TradeExitReason.Target, PositionSide side = PositionSide.Long)
    {
        var direction = side == PositionSide.Long ? 1m : -1m;
        await using var db = fixture.CreateContext();
        var trade = new Trade
        {
            AgentId = agent.Id, EntryDecisionId = decision.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe,
            Side = side, EntryPrice = entry, ExitPrice = exit, Size = size, CurrentStop = stop,
            StopHistory = JsonSerializer.SerializeToElement(new[] { new { at = closedAt.AddHours(-3), price = stop, reason = "initial" } }),
            Targets = JsonSerializer.SerializeToElement(new[] { 120m }), Fees = fees, Funding = 0m,
            RealizedPnl = (exit - entry) * direction * size - fees, RMultiple = (exit - entry) * direction / Math.Abs(entry - stop),
            MaxAdverseExcursion = 0.01m, MaxFavorableExcursion = 0.2m, ExitReason = reason, IsPaper = true, OpenedAt = closedAt.AddHours(-3), ClosedAt = closedAt,
        };
        db.Add(trade);
        await db.SaveChangesAsync(Ct);
        return trade;
    }

    /// <summary>A shadow outcome of the decision; resolved ones carry their R, pending ones a walkable detail unless <paramref name="withDetail"/> is false.</summary>
    public static async Task<ShadowOutcome> NewShadowAsync(DatabaseLiveFixture fixture, Decision decision, DateTimeOffset candleOpen, ShadowResult result, decimal? r = null, bool withDetail = true,
        string reason = ShadowDetail.Veto, int horizon = 30)
    {
        await using var db = fixture.CreateContext();
        var detail = new ShadowDetail
        {
            Reason = reason, Side = PositionSide.Long, EntryType = OrderType.Limit, Entry = 100m, Stop = 90m, Target = 120m, DecisionCandleOpenTime = candleOpen,
            EntryTimeoutCandles = 1, HorizonCandles = horizon, Resolution = result switch { ShadowResult.HitStop => ShadowDetail.ByStop, ShadowResult.HitTarget => ShadowDetail.ByTarget, _ => null },
        };
        var shadow = new ShadowOutcome
        {
            DecisionId = decision.Id, Result = result, RMultiple = r, ResolvedAt = result == ShadowResult.Pending ? null : candleOpen.AddHours(5),
            Detail = withDetail ? detail.ToJson() : null,
        };
        db.Add(shadow);
        await db.SaveChangesAsync(Ct);
        return shadow;
    }

    public static async Task<List<ExperienceStat>> StatsAsync(DatabaseLiveFixture fixture)
    {
        await using var db = fixture.CreateContext();
        return await db.ExperienceStats.AsNoTracking().OrderBy(s => s.Id).ToListAsync(Ct);
    }

    public static async Task<ShadowOutcome> ShadowAsync(DatabaseLiveFixture fixture, Guid decisionId)
    {
        await using var db = fixture.CreateContext();
        return await db.ShadowOutcomes.AsNoTracking().SingleAsync(s => s.DecisionId == decisionId, Ct);
    }

    public static async Task AddCatalogModelsAsync(DatabaseLiveFixture fixture, DateTimeOffset now, params (AiProvider Provider, string ModelId, ModelStatus Status)[] models)
    {
        await using var db = fixture.CreateContext();
        foreach (var (provider, modelId, status) in models)
        {
            db.Add(new ProviderModel
            {
                Provider = provider, ModelId = modelId, DisplayName = modelId, Status = status, ProviderCreatedAt = now.AddDays(-10), FirstSeenAt = now.AddDays(-10), LastSeenAt = now,
                SupportsTextGeneration = true, Capabilities = JsonValues.EmptyObject,
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    public static async Task AddStatAsync(DatabaseLiveFixture fixture, StatScope scope, Guid? userId, Guid? agentId, AiProvider provider, string model, CouncilRole role,
        CandleInterval timeframe, MarketType market, ExperienceMetrics metrics, DateTimeOffset computedAt)
    {
        await using var db = fixture.CreateContext();
        db.Add(new ExperienceStat
        {
            Scope = scope, UserId = userId, AgentId = agentId, Provider = provider, Model = model, Role = role, Timeframe = timeframe, MarketType = market,
            Metrics = metrics.Derive(), ComputedAt = computedAt,
        });
        await db.SaveChangesAsync(Ct);
    }
}
