using System.Text.Json;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Council.Services;

/// <summary>Reads one evaluation with its decision, role outputs and transcripts for the owning user (FR-AI-14, NFR-SEC-03).</summary>
internal sealed class DeliberationReader : IDeliberationReader
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public DeliberationReader(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<DeliberationView?> GetAsync(Guid userId, Guid evaluationId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var evaluation = await db.Evaluations.AsNoTracking()
            .Where(e => e.Id == evaluationId && e.Agent.UserId == userId)
            .Include(e => e.Agent)
            .Include(e => e.Decision).ThenInclude(d => d!.RoleOutputs)
            .Include(e => e.Decision).ThenInclude(d => d!.Transcripts)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (evaluation is null)
            return null;

        var turns = new List<RoleTurnView>();
        FinalDecisionView? decision = null;
        if (evaluation.Decision is { } stored)
        {
            foreach (var output in stored.RoleOutputs.OrderBy(o => o.Sequence))
            {
                var transcript = stored.Transcripts.FirstOrDefault(t => t.Role == output.Role);
                turns.Add(Turn(output, transcript));
            }

            decision = new FinalDecisionView(
                stored.FinalAction, stored.EntryType, stored.EntryPrice, stored.StopPrice, Targets(stored.Targets), stored.PartialFraction, stored.RiskPercent,
                stored.Confidence, stored.Rationale, Limits(stored.LimitsCheck), stored.TotalInputTokens, stored.TotalOutputTokens, stored.TotalLatencyMs, stored.CreatedAt);
        }

        return new DeliberationView(
            evaluation.Id, evaluation.AgentId, evaluation.Agent.Name, evaluation.Symbol, evaluation.MarketType, evaluation.Timeframe, evaluation.CandleOpenTime,
            evaluation.StartedAt, evaluation.FinishedAt, evaluation.Outcome, evaluation.OutcomeDetail, evaluation.CouncilConsulted, evaluation.StrategyOutput, decision, turns);
    }

    private static RoleTurnView Turn(DecisionRoleOutput output, DeliberationTranscript? transcript)
    {
        var attempts = 0;
        string? error = null;
        JsonElement? document = null;
        if (transcript is { Output.ValueKind: JsonValueKind.Object } t)
        {
            if (t.Output.TryGetProperty("attempts", out var a) && a.ValueKind == JsonValueKind.Number)
                attempts = a.GetInt32();
            if (t.Output.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                error = e.GetString();
            if (t.Output.TryGetProperty("document", out var d) && d.ValueKind == JsonValueKind.Object)
                document = d.Clone();
        }

        return new RoleTurnView(output.Role, output.Sequence, output.Provider, output.Model, output.Status, output.ProposedAction, output.Confidence,
            output.InputTokens, output.OutputTokens, output.LatencyMs, attempts, error, document, string.IsNullOrEmpty(transcript?.InputSummary) ? null : transcript!.InputSummary);
    }

    private static IReadOnlyList<decimal> Targets(JsonElement? targets)
    {
        if (targets is not { ValueKind: JsonValueKind.Array } array)
            return [];
        var result = new List<decimal>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var price))
                result.Add(price);
        }

        return result;
    }

    private static IReadOnlyList<LimitCheck> Limits(JsonElement? limits)
    {
        if (limits is not { ValueKind: JsonValueKind.Array } array)
            return [];
        var result = new List<LimitCheck>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var limit = item.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "";
            var status = item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && Enum.TryParse<LimitCheckStatus>(s.GetString(), ignoreCase: true, out var parsed) ? parsed : LimitCheckStatus.Pass;
            var detail = item.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            result.Add(new LimitCheck(limit, status, detail));
        }

        return result;
    }
}
