using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Experience.Services;

/// <summary>
/// The pipeline's experience step (FR-EXP-03): advances this agent's pending shadow outcomes over the candles the
/// evaluation just saw, then opens one for a setup the council did not take. No market data is fetched: the snapshot
/// the evaluation used is walked, in the environment the agent trades in, so a shadow never looks ahead.
/// </summary>
internal sealed class ExperienceTracker : IExperienceTracker
{
    private const int DefaultEntryTimeoutCandles = 1;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<ExperienceOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExperienceTracker> _logger;

    public ExperienceTracker(IDbContextFactory<EcnanibDbContext> factory, IOptions<ExperienceOptions> options, TimeProvider time, ILogger<ExperienceTracker> logger)
    {
        _factory = factory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task TrackAsync(ExperienceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await AdvanceAsync(context, cancellationToken).ConfigureAwait(false);
        await OpenAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Why a decision leaves a shadow, or null when it leaves none: a setup existed, entries were allowed and nothing was
    /// opened, because the Risk Manager vetoed it, the hard limits rejected its entry, or no decision was made (FR-AI-12).
    /// </summary>
    internal static string? ReasonFor(Decision decision, StrategyOutput output, bool entriesAllowed)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(output);
        if (!entriesAllowed || output.Setup is null || decision.FinalAction != DecisionAction.NoTrade)
            return null;
        var final = decision.RoleOutputs.FirstOrDefault(r => r.Role == CouncilRoles.Final);
        if (final is null || final.Status != RoleOutputStatus.Succeeded)
            return ShadowDetail.NoDecision;
        return final.ProposedAction == DecisionAction.NoTrade ? ShadowDetail.Veto : ShadowDetail.Limits;
    }

    /// <summary>The strategy's entry timeout, which every Appendix A strategy exposes as a common parameter.</summary>
    internal static int EntryTimeout(IReadOnlyDictionary<string, JsonElement> parameters) =>
        parameters.TryGetValue(StrategyParameters.EntryTimeoutCandles, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var candles) && candles > 0
            ? candles
            : DefaultEntryTimeoutCandles;

    private async Task OpenAsync(ExperienceContext context, CancellationToken cancellationToken)
    {
        if (context.Decision is not { } decision || ReasonFor(decision, context.Output, context.EntriesAllowed) is not { } reason)
            return;
        var setup = context.Output.Setup!;
        if (setup.Targets.Count == 0)
        {
            _logger.LogDebug("Decision {DecisionId} of agent {AgentId} rejected a setup without a target; no shadow outcome", decision.Id, context.Agent.Id);
            return;
        }

        var detail = new ShadowDetail
        {
            Reason = reason,
            Side = setup.Side,
            EntryType = setup.EntryType,
            Entry = setup.Entry,
            Stop = setup.Stop,
            Target = setup.Targets[0],
            DecisionCandleOpenTime = context.Evaluation.CandleOpenTime,
            EntryTimeoutCandles = EntryTimeout(context.Parameters),
            HorizonCandles = _options.Value.ShadowHorizonCandles,
        };

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ShadowOutcomes.Add(new ShadowOutcome { DecisionId = decision.Id, Result = ShadowResult.Pending, Detail = detail.ToJson() });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (CandleStore.IsUniqueViolation(ex))
        {
            return;
        }

        _logger.LogInformation("Shadow outcome opened for decision {DecisionId} of agent {AgentId} ({Reason}): {Side} entry {Entry}, stop {Stop}, target {Target}",
            decision.Id, context.Agent.Id, reason, setup.Side, setup.Entry, setup.Stop, setup.Targets[0]);
    }

    private async Task AdvanceAsync(ExperienceContext context, CancellationToken cancellationToken)
    {
        var evaluation = context.Evaluation;
        var agentId = context.Agent.Id;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.ShadowOutcomes
            .Where(s => s.Result == ShadowResult.Pending
                        && s.Decision.Evaluation.AgentId == agentId
                        && s.Decision.Evaluation.Symbol == evaluation.Symbol
                        && s.Decision.Evaluation.MarketType == evaluation.MarketType
                        && s.Decision.Evaluation.Timeframe == evaluation.Timeframe)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
            return;

        var candles = context.Market.Candles;
        var span = evaluation.Timeframe.ToTimeSpan();
        var earliest = candles.Count == 0 ? (DateTimeOffset?)null : candles.Min(c => c.OpenTime);
        var now = _time.GetUtcNow();
        var resolved = 0;
        foreach (var shadow in pending)
        {
            var detail = ShadowDetail.Parse(shadow.Detail);
            var step = detail is null ? ShadowWalk.Expire(null, ShadowDetail.NoDetail)
                : earliest is null || earliest > detail.DecisionCandleOpenTime + span ? ShadowWalk.Expire(detail, ShadowDetail.Stale)
                : ShadowWalk.Walk(detail, candles);
            shadow.Detail = step.Detail.ToJson();
            if (step.Result == ShadowResult.Pending)
                continue;
            shadow.Result = step.Result;
            shadow.RMultiple = step.RMultiple;
            shadow.ResolvedAt = now;
            resolved++;
            _logger.LogInformation("Shadow outcome of decision {DecisionId} for agent {AgentId} resolved {Result} ({Resolution}) at {R}R after {Candles} candle(s)",
                shadow.DecisionId, agentId, step.Result, step.Detail.Resolution, step.RMultiple, step.Detail.CandlesSeen);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (resolved == 0)
            _logger.LogDebug("Agent {AgentId} has {Pending} pending shadow outcome(s) after evaluation {EvaluationId}", agentId, pending.Count, evaluation.Id);
    }
}
