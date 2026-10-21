using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Experience.Services;

/// <summary>The finest key of a statistic: one agent's use of one provider, model and role at one timeframe and market (FR-EXP-04).</summary>
internal readonly record struct AgentStatKey(Guid AgentId, Guid UserId, AiProvider Provider, string Model, CouncilRole Role, CandleInterval Timeframe, MarketType MarketType);

/// <summary>Where an owner's gross loss is summed: per agent, timeframe and market, the denominator of the drawdown contribution.</summary>
internal readonly record struct LossKey(Guid AgentId, Guid UserId, CandleInterval Timeframe, MarketType MarketType);

/// <summary>
/// Computes the experience statistics from the decisions, trades and shadow outcomes (FR-EXP-04, FR-EXP-05): the
/// counts and sums are gathered per agent and rolled up to the user and system scopes, so every scope is the exact
/// sum of the rows below it. Turns are grouped by the database; trades and shadows are few and are joined to the
/// role outputs that proposed them in memory, where the trade's placed stop gives its net R.
/// </summary>
internal static class ExperienceAggregates
{
    /// <summary>A jsonb containment probe: the limits check holds an entry the hard limits clamped (FR-AI-11).</summary>
    internal const string ClampedProbe = """[{"status":"clamped"}]""";

    private const int RDecimals = 6;

    public static async Task<List<ExperienceStat>> ComputeAsync(EcnanibDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = new Dictionary<AgentStatKey, ExperienceMetrics>();
        await AddTurnsAsync(db, rows, cancellationToken).ConfigureAwait(false);
        await AddClampsAsync(db, rows, cancellationToken).ConfigureAwait(false);
        await AddTradesAsync(db, rows, cancellationToken).ConfigureAwait(false);
        await AddShadowsAsync(db, rows, cancellationToken).ConfigureAwait(false);
        var losses = await LossesAsync(db, cancellationToken).ConfigureAwait(false);
        return Assemble(rows, losses, now);
    }

    /// <summary>Agent rows with their owner's loss denominator, rolled up to one row per user and one system-wide (FR-EXP-05), every ratio derived.</summary>
    internal static List<ExperienceStat> Assemble(IReadOnlyDictionary<AgentStatKey, ExperienceMetrics> agentRows, IReadOnlyDictionary<LossKey, decimal> losses, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agentRows);
        ArgumentNullException.ThrowIfNull(losses);
        var userLosses = new Dictionary<(Guid UserId, CandleInterval Timeframe, MarketType MarketType), decimal>();
        var systemLosses = new Dictionary<(CandleInterval Timeframe, MarketType MarketType), decimal>();
        foreach (var (key, loss) in losses)
        {
            userLosses[(key.UserId, key.Timeframe, key.MarketType)] = userLosses.GetValueOrDefault((key.UserId, key.Timeframe, key.MarketType)) + loss;
            systemLosses[(key.Timeframe, key.MarketType)] = systemLosses.GetValueOrDefault((key.Timeframe, key.MarketType)) + loss;
        }

        var result = new List<ExperienceStat>();
        var users = new Dictionary<(Guid UserId, AiProvider Provider, string Model, CouncilRole Role, CandleInterval Timeframe, MarketType MarketType), ExperienceMetrics>();
        var system = new Dictionary<(AiProvider Provider, string Model, CouncilRole Role, CandleInterval Timeframe, MarketType MarketType), ExperienceMetrics>();
        foreach (var (key, metrics) in agentRows.OrderBy(r => r.Key.AgentId).ThenBy(r => r.Key.Provider).ThenBy(r => r.Key.Model, StringComparer.Ordinal).ThenBy(r => r.Key.Role))
        {
            var userKey = (key.UserId, key.Provider, key.Model, key.Role, key.Timeframe, key.MarketType);
            users[userKey] = users.TryGetValue(userKey, out var user) ? user.Add(metrics) : metrics.Clone();
            var systemKey = (key.Provider, key.Model, key.Role, key.Timeframe, key.MarketType);
            system[systemKey] = system.TryGetValue(systemKey, out var all) ? all.Add(metrics) : metrics.Clone();

            metrics.ScopeGrossLossR = losses.GetValueOrDefault(new LossKey(key.AgentId, key.UserId, key.Timeframe, key.MarketType));
            result.Add(new ExperienceStat
            {
                Scope = StatScope.Agent, AgentId = key.AgentId, UserId = key.UserId, Provider = key.Provider, Model = key.Model, Role = key.Role,
                Timeframe = key.Timeframe, MarketType = key.MarketType, Metrics = metrics.Derive(), ComputedAt = now,
            });
        }

        foreach (var (key, metrics) in users)
        {
            metrics.ScopeGrossLossR = userLosses.GetValueOrDefault((key.UserId, key.Timeframe, key.MarketType));
            result.Add(new ExperienceStat
            {
                Scope = StatScope.User, UserId = key.UserId, Provider = key.Provider, Model = key.Model, Role = key.Role,
                Timeframe = key.Timeframe, MarketType = key.MarketType, Metrics = metrics.Derive(), ComputedAt = now,
            });
        }

        foreach (var (key, metrics) in system)
        {
            metrics.ScopeGrossLossR = systemLosses.GetValueOrDefault((key.Timeframe, key.MarketType));
            result.Add(new ExperienceStat
            {
                Scope = StatScope.System, Provider = key.Provider, Model = key.Model, Role = key.Role,
                Timeframe = key.Timeframe, MarketType = key.MarketType, Metrics = metrics.Derive(), ComputedAt = now,
            });
        }

        return result;
    }

    /// <summary>The initial stop a closed trade was placed with: the first entry of its stop history, else the decision's stop.</summary>
    internal static decimal? InitialStop(JsonElement stopHistory, decimal? decisionStop)
    {
        if (stopHistory.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in stopHistory.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var value) && value > 0)
                    return value;
                break;
            }
        }

        return decisionStop is > 0 ? decisionStop : null;
    }

    /// <summary>Records one closed trade on the row of a role that proposed its entry (gross and net R), or on its rejection metrics when the role said no_trade.</summary>
    internal static void RecordTrade(ExperienceMetrics metrics, DecisionAction proposed, decimal r, decimal? realizedPnl, decimal entryPrice, decimal size, decimal? initialStop)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (proposed is DecisionAction.OpenLong or DecisionAction.OpenShort)
        {
            metrics.Trades++;
            if (r > 0)
                metrics.Wins++;
            else if (r < 0)
                metrics.Losses++;
            metrics.SumR += r;
            if (r > 0)
                metrics.GrossProfitR += r;
            else
                metrics.GrossLossR += -r;
            var risk = initialStop is { } stop ? Math.Abs(entryPrice - stop) * size : 0m;
            if (realizedPnl is { } pnl && risk > 0)
            {
                metrics.NetTrades++;
                metrics.SumNetR += Math.Round(pnl / risk, RDecimals, MidpointRounding.AwayFromZero);
            }
        }
        else if (proposed == DecisionAction.NoTrade)
        {
            metrics.RejectionsResolved++;
            if (r > 0)
                metrics.RejectionsMissedWins++;
            metrics.RejectionsSumR += r;
        }
    }

    /// <summary>Records one resolved shadow outcome on the rejection metrics of a role that proposed no_trade.</summary>
    internal static void RecordShadow(ExperienceMetrics metrics, ShadowResult result, decimal r)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        metrics.RejectionsResolved++;
        if (result == ShadowResult.HitTarget)
            metrics.RejectionsMissedWins++;
        metrics.RejectionsSumR += r;
    }

    private static async Task AddTurnsAsync(EcnanibDbContext db, Dictionary<AgentStatKey, ExperienceMetrics> rows, CancellationToken cancellationToken)
    {
        var turns = await db.DecisionRoleOutputs.AsNoTracking()
            .Where(r => r.Status == RoleOutputStatus.Succeeded || r.Status == RoleOutputStatus.Failed || r.Status == RoleOutputStatus.TimedOut)
            .Select(r => new
            {
                r.Decision.Evaluation.AgentId,
                r.Decision.Evaluation.Agent.UserId,
                r.Provider,
                r.Model,
                r.Role,
                r.Decision.Evaluation.Timeframe,
                r.Decision.Evaluation.MarketType,
                r.Status,
                r.ProposedAction,
                r.LatencyMs,
                Final = r.Decision.FinalAction,
                HasShadow = r.Decision.ShadowOutcome != null,
            })
            .GroupBy(x => new { x.AgentId, x.UserId, x.Provider, x.Model, x.Role, x.Timeframe, x.MarketType })
            .Select(g => new
            {
                g.Key,
                Decisions = g.Count(),
                Approvals = g.Sum(x => x.ProposedAction == DecisionAction.OpenLong || x.ProposedAction == DecisionAction.OpenShort ? 1 : 0),
                Rejections = g.Sum(x => x.ProposedAction == DecisionAction.NoTrade && (x.Final == DecisionAction.OpenLong || x.Final == DecisionAction.OpenShort || x.HasShadow) ? 1 : 0),
                Failures = g.Sum(x => x.Status == RoleOutputStatus.Failed || x.Status == RoleOutputStatus.TimedOut ? 1 : 0),
                Latency = g.Sum(x => (long)x.LatencyMs),
                RiskManagerTurns = g.Sum(x => x.Role == CouncilRole.RiskManager && x.Status == RoleOutputStatus.Succeeded ? 1 : 0),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var turn in turns)
        {
            var metrics = Row(rows, new AgentStatKey(turn.Key.AgentId, turn.Key.UserId, turn.Key.Provider, turn.Key.Model, turn.Key.Role, turn.Key.Timeframe, turn.Key.MarketType));
            metrics.Decisions += turn.Decisions;
            metrics.Approvals += turn.Approvals;
            metrics.Rejections += turn.Rejections;
            metrics.Failures += turn.Failures;
            metrics.TotalLatencyMs += turn.Latency;
            metrics.RiskManagerTurns += turn.RiskManagerTurns;
        }
    }

    private static async Task AddClampsAsync(EcnanibDbContext db, Dictionary<AgentStatKey, ExperienceMetrics> rows, CancellationToken cancellationToken)
    {
        var clamps = await db.DecisionRoleOutputs.AsNoTracking()
            .Where(r => r.Role == CouncilRole.RiskManager && r.Status == RoleOutputStatus.Succeeded && r.Decision.LimitsCheck != null
                        && EF.Functions.JsonContains(r.Decision.LimitsCheck!.Value, ClampedProbe))
            .GroupBy(r => new
            {
                r.Decision.Evaluation.AgentId,
                r.Decision.Evaluation.Agent.UserId,
                r.Provider,
                r.Model,
                r.Decision.Evaluation.Timeframe,
                r.Decision.Evaluation.MarketType,
            })
            .Select(g => new { g.Key, Clamped = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var clamp in clamps)
            Row(rows, new AgentStatKey(clamp.Key.AgentId, clamp.Key.UserId, clamp.Key.Provider, clamp.Key.Model, CouncilRole.RiskManager, clamp.Key.Timeframe, clamp.Key.MarketType)).Clamped += clamp.Clamped;
    }

    private static async Task AddTradesAsync(EcnanibDbContext db, Dictionary<AgentStatKey, ExperienceMetrics> rows, CancellationToken cancellationToken)
    {
        var trades = await db.Trades.AsNoTracking()
            .Where(t => t.ClosedAt != null && t.RMultiple != null)
            .SelectMany(t => t.EntryDecision.RoleOutputs.Where(r => r.Status == RoleOutputStatus.Succeeded && r.ProposedAction != null), (t, r) => new
            {
                t.AgentId,
                t.Agent.UserId,
                t.Timeframe,
                t.MarketType,
                t.RMultiple,
                t.RealizedPnl,
                t.EntryPrice,
                t.Size,
                t.StopHistory,
                DecisionStop = t.EntryDecision.StopPrice,
                r.Provider,
                r.Model,
                r.Role,
                r.ProposedAction,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var trade in trades)
        {
            var metrics = Row(rows, new AgentStatKey(trade.AgentId, trade.UserId, trade.Provider, trade.Model, trade.Role, trade.Timeframe, trade.MarketType));
            RecordTrade(metrics, trade.ProposedAction!.Value, trade.RMultiple!.Value, trade.RealizedPnl, trade.EntryPrice, trade.Size, InitialStop(trade.StopHistory, trade.DecisionStop));
        }
    }

    private static async Task AddShadowsAsync(EcnanibDbContext db, Dictionary<AgentStatKey, ExperienceMetrics> rows, CancellationToken cancellationToken)
    {
        var shadows = await db.ShadowOutcomes.AsNoTracking()
            .Where(s => (s.Result == ShadowResult.HitTarget || s.Result == ShadowResult.HitStop) && s.RMultiple != null)
            .SelectMany(s => s.Decision.RoleOutputs.Where(r => r.Status == RoleOutputStatus.Succeeded && r.ProposedAction == DecisionAction.NoTrade), (s, r) => new
            {
                s.Decision.Evaluation.AgentId,
                s.Decision.Evaluation.Agent.UserId,
                s.Decision.Evaluation.Timeframe,
                s.Decision.Evaluation.MarketType,
                s.Result,
                s.RMultiple,
                r.Provider,
                r.Model,
                r.Role,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var shadow in shadows)
            RecordShadow(Row(rows, new AgentStatKey(shadow.AgentId, shadow.UserId, shadow.Provider, shadow.Model, shadow.Role, shadow.Timeframe, shadow.MarketType)), shadow.Result, shadow.RMultiple!.Value);
    }

    /// <summary>Gross loss (R) per agent, timeframe and market over distinct closed trades, so a loss counts once however many roles proposed it.</summary>
    private static async Task<Dictionary<LossKey, decimal>> LossesAsync(EcnanibDbContext db, CancellationToken cancellationToken)
    {
        var losses = await db.Trades.AsNoTracking()
            .Where(t => t.ClosedAt != null && t.RMultiple != null && t.RMultiple < 0)
            .GroupBy(t => new { t.AgentId, t.Agent.UserId, t.Timeframe, t.MarketType })
            .Select(g => new { g.Key, GrossLossR = g.Sum(t => -t.RMultiple!.Value) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return losses.ToDictionary(l => new LossKey(l.Key.AgentId, l.Key.UserId, l.Key.Timeframe, l.Key.MarketType), l => l.GrossLossR);
    }

    private static ExperienceMetrics Row(Dictionary<AgentStatKey, ExperienceMetrics> rows, AgentStatKey key)
    {
        if (!rows.TryGetValue(key, out var metrics))
            rows[key] = metrics = new ExperienceMetrics();
        return metrics;
    }
}
