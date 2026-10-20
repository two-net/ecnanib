using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Council.Services;

/// <summary>
/// The sequential council of Appendix B (FR-AI-01..07, FR-AI-09..15). Roles run in order with the agent's provider
/// and model for each (FR-AI-03), every call under the user's own key (AI-03) within a budget carved out of the
/// evaluation deadline so that the Risk Manager always gets its turn (FR-AI-13). A role whose answer fails
/// validation is asked once more and then counts as failed; the roles after it are told (FR-AI-07, FR-AI-12). The
/// Risk Manager's document goes through the hard limits (FR-AI-11) into the decision; every turn is recorded with
/// its input, output, provider, model, latency and tokens (FR-AI-14). Without a Risk Manager decision the result is a
/// failure that still carries the deliberation; a model the provider rejects is a fault that stops the council
/// (FR-SYNC-04).
/// </summary>
internal sealed class Council : ICouncil
{
    /// <summary>Raw answer text kept in a transcript; the parsed document is stored in full.</summary>
    internal const int RawTextLimit = 4000;

    private const int RationaleColumnLength = 4000;

    private static readonly JsonSerializerOptions TranscriptJson = new(JsonSerializerDefaults.Web);

    private readonly IAiClientFactory _ai;
    private readonly IKeyResolver _keys;
    private readonly IExperienceSummaries _experience;
    private readonly IOptions<CouncilOptions> _options;
    private readonly IOptions<RiskCapsOptions> _caps;
    private readonly TimeProvider _time;
    private readonly ILogger<Council> _logger;

    public Council(
        IAiClientFactory ai,
        IKeyResolver keys,
        IExperienceSummaries experience,
        IOptions<CouncilOptions> options,
        IOptions<RiskCapsOptions> caps,
        TimeProvider time,
        ILogger<Council> logger)
    {
        _ai = ai;
        _keys = keys;
        _experience = experience;
        _options = options;
        _caps = caps;
        _time = time;
        _logger = logger;
    }

    public async Task<CouncilResult> DeliberateAsync(CouncilContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = _options.Value;
        var agent = context.Agent;
        var evaluationId = context.Evaluation.Id;

        var riskManager = agent.CouncilAssignments.FirstOrDefault(a => a.Role == CouncilRoles.Final);
        if (riskManager is null || !riskManager.Enabled)
            return CouncilResult.Failed("the agent has no enabled Risk Manager");

        var keys = new Dictionary<AiProvider, KeyResolution<AiKeyHandle>>();
        var turns = new List<Turn>();
        var preceding = new List<PrecedingTurn>();
        string? stopReason = null;
        string? fault = null;

        foreach (var role in CouncilRoles.Order)
        {
            var final = role == CouncilRoles.Final;
            var assignment = agent.CouncilAssignments.FirstOrDefault(a => a.Role == role);
            var turn = new Turn(role, assignment?.Provider ?? CouncilRoles.DefaultProvider(role), assignment?.Model.Trim() ?? "");
            turns.Add(turn);

            if (assignment is null || !assignment.Enabled)
            {
                turn.Status = RoleOutputStatus.Disabled;
                turn.Error = assignment is null ? "no assignment for this role" : null;
            }
            else if (stopReason is not null)
            {
                turn.Status = RoleOutputStatus.Skipped;
                turn.Error = stopReason;
            }
            else if (Budget(options, context.DeadlineAt, _time.GetUtcNow(), final) is not { } budget)
            {
                turn.Status = final ? RoleOutputStatus.TimedOut : RoleOutputStatus.Skipped;
                turn.Error = "not enough time left before the evaluation deadline";
            }
            else if (turn.Model.Length == 0)
            {
                turn.Status = RoleOutputStatus.Failed;
                turn.Error = "no model is configured for this role";
            }
            else
            {
                var key = await ResolveKeyAsync(keys, agent.UserId, turn.Provider, cancellationToken).ConfigureAwait(false);
                if (!key.IsResolved)
                {
                    turn.Status = RoleOutputStatus.Failed;
                    turn.Error = key.Reason ?? $"no valid {ProviderNames.Display(turn.Provider)} key";
                }
                else
                {
                    var experience = await SummarizeAsync(context, turn, cancellationToken).ConfigureAwait(false);
                    turn.Prompt = RolePrompts.User(context, role, experience, preceding, options);
                    await RunAsync(turn, context, key.Handle!, RolePrompts.System(role, options), budget, final, options, cancellationToken).ConfigureAwait(false);
                }
            }

            if (turn.Fault is not null)
            {
                fault = turn.Fault;
                stopReason = "the council stopped: " + fault;
            }
            else if (turn.DeadlinePassed)
            {
                stopReason = "the evaluation deadline passed";
            }

            preceding.Add(new PrecedingTurn(role, turn.Status, turn.Error, turn.Document));
            Log(turn, agent.Id, evaluationId);
        }

        var now = _time.GetUtcNow();
        var decision = new Decision
        {
            Rationale = "",
            CreatedAt = now,
            TotalInputTokens = turns.Sum(t => t.InputTokens),
            TotalOutputTokens = turns.Sum(t => t.OutputTokens),
            TotalLatencyMs = (int)Math.Min(int.MaxValue, turns.Sum(t => t.Latency.TotalMilliseconds)),
        };
        foreach (var turn in turns)
        {
            decision.RoleOutputs.Add(new DecisionRoleOutput
            {
                Role = turn.Role,
                Sequence = CouncilRoles.Sequence(turn.Role),
                Provider = turn.Provider,
                Model = turn.Model,
                Status = turn.Status,
                ProposedAction = turn.Document?.ProposedAction,
                Confidence = turn.Document?.Confidence,
                InputTokens = turn.InputTokens,
                OutputTokens = turn.OutputTokens,
                LatencyMs = (int)Math.Min(int.MaxValue, turn.Latency.TotalMilliseconds),
            });
            if (turn.Status != RoleOutputStatus.Disabled)
                decision.Transcripts.Add(new DeliberationTranscript { Role = turn.Role, InputSummary = turn.Prompt ?? "", Output = TranscriptOutput(turn), CreatedAt = now });
        }

        var last = turns.Single(t => t.Role == CouncilRoles.Final);
        if (last.Document is FinalDecisionDocument document)
        {
            var outcome = HardLimits.Apply(document, context, _caps.Value);
            decision.FinalAction = outcome.Action;
            decision.EntryType = outcome.EntryType;
            decision.EntryPrice = outcome.EntryPrice;
            decision.StopPrice = outcome.StopPrice;
            decision.Targets = JsonSerializer.SerializeToElement(outcome.Targets);
            decision.PartialFraction = outcome.PartialFraction;
            decision.RiskPercent = outcome.RiskPercent;
            decision.Confidence = document.Confidence;
            decision.LimitsCheck = HardLimits.ToJson(outcome.Checks);
            decision.Rationale = Truncate(outcome.Rejected ? $"Rejected by hard limits ({string.Join(", ", outcome.FailedLimits)}): {document.Rationale}" : document.Rationale, RationaleColumnLength);
            if (outcome.Action is DecisionAction.Hold or DecisionAction.Modify or DecisionAction.Close && context.OpenTrades.Count > 0)
                decision.TradeId = context.OpenTrades[0].Id;

            var notable = outcome.Checks.Where(c => c.Status != LimitCheckStatus.Pass).Select(c => $"{c.Limit} {c.Status.ToString().ToLowerInvariant()}").ToList();
            _logger.LogInformation("Council decided {Action} for agent {AgentId} evaluation {EvaluationId} (proposed {Proposed}, confidence {Confidence}): {Limits}",
                decision.FinalAction, agent.Id, evaluationId, document.ProposedAction, document.Confidence, notable.Count == 0 ? "all limits pass" : string.Join(", ", notable));
            return CouncilResult.Decided(decision);
        }

        var reason = $"the Risk Manager {RolePrompts.Describe(last.Status)}{(last.Error is null ? "" : ": " + last.Error)}";
        decision.FinalAction = DecisionAction.NoTrade;
        decision.Rationale = Truncate($"No decision: {reason}", RationaleColumnLength);
        _logger.LogWarning("Council produced no decision for agent {AgentId} evaluation {EvaluationId}: {Reason}", agent.Id, evaluationId, reason);
        return CouncilResult.Failed(reason, decision, fault);
    }

    /// <summary>
    /// Time a role may use: its timeout, or what is left before the deadline (less a margin, and less the Risk
    /// Manager's reserve for a non-final role); null when that is below the configured minimum (FR-AI-13).
    /// </summary>
    internal static TimeSpan? Budget(CouncilOptions options, DateTimeOffset deadlineAt, DateTimeOffset now, bool final)
    {
        var remaining = deadlineAt - now - CouncilOptions.DeadlineMargin;
        if (!final)
            remaining -= options.RiskManagerReserve;
        var budget = remaining < options.RoleTimeout ? remaining : options.RoleTimeout;
        return budget < options.MinimumRoleBudget ? null : budget;
    }

    private async Task RunAsync(Turn turn, CouncilContext context, AiKeyHandle key, string system, TimeSpan budget, bool final, CouncilOptions options, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(budget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        var client = _ai.GetClient(turn.Provider);
        var schema = CouncilSchemas.For(final);
        var prompt = turn.Prompt!;
        var user = prompt;
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var request = new AiRequest(turn.Model, system, user, schema, options.MaxOutputTokens, turn.Role.ToString());
                var completion = await client.CompleteAsync(key, request, linked.Token).ConfigureAwait(false);
                turn.Attempts = attempt;
                turn.InputTokens += completion.Usage.InputTokens;
                turn.OutputTokens += completion.Usage.OutputTokens;
                turn.ReasoningTokens += completion.Usage.ReasoningTokens ?? 0;
                turn.Latency += completion.Latency;
                turn.FinishReason = completion.RawFinishReason ?? completion.FinishReason.ToString();
                turn.Raw = Truncate(completion.Text, RawTextLimit);

                string? rejection;
                RoleOutputDocument? document = null;
                if (completion.FinishReason != AiFinishReason.Stop)
                    rejection = $"the answer ended with {Describe(completion.FinishReason)} instead of a complete JSON document";
                else if (!completion.IsStructured)
                    rejection = "the answer was not the JSON document that was requested";
                else
                    document = RoleOutputParser.Parse(completion.Text, final, options, out rejection);

                if (document is not null)
                {
                    turn.Status = RoleOutputStatus.Succeeded;
                    turn.Document = document;
                    return;
                }

                if (attempt == 1)
                {
                    user = prompt + $"\n\n## Correction\nYour previous answer was rejected: {rejection}. Answer again with exactly one JSON document matching the schema.";
                    continue;
                }

                turn.Status = RoleOutputStatus.Failed;
                turn.Error = $"the answer was rejected twice: {rejection}";
            }
        }
        catch (AiRequestException ex) when (ex.Kind == AiErrorKind.UnknownModel)
        {
            turn.Status = RoleOutputStatus.Failed;
            turn.Error = ex.Message;
            turn.Fault = $"The {CouncilRoles.Display(turn.Role)} role's model '{turn.Model}' was rejected by {ProviderNames.Display(turn.Provider)}: {ex.Message}";
        }
        catch (AiException ex)
        {
            turn.Status = RoleOutputStatus.Failed;
            turn.Error = $"{ProviderNames.Display(turn.Provider)} {ex.Kind}: {ex.Message}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            turn.Status = RoleOutputStatus.TimedOut;
            turn.Error = $"no valid answer within the role's budget of {(int)budget.TotalSeconds} s";
        }
        catch (OperationCanceledException) when (_time.GetUtcNow() >= context.DeadlineAt - CouncilOptions.DeadlineMargin)
        {
            turn.Status = RoleOutputStatus.TimedOut;
            turn.DeadlinePassed = true;
            turn.Error = "the evaluation deadline passed";
        }
    }

    private async Task<KeyResolution<AiKeyHandle>> ResolveKeyAsync(Dictionary<AiProvider, KeyResolution<AiKeyHandle>> cache, Guid userId, AiProvider provider, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(provider, out var cached))
            return cached;
        var resolution = await _keys.ResolveProviderKeyAsync(userId, provider, cancellationToken).ConfigureAwait(false);
        cache[provider] = resolution;
        return resolution;
    }

    private async Task<string?> SummarizeAsync(CouncilContext context, Turn turn, CancellationToken cancellationToken)
    {
        var agent = context.Agent;
        try
        {
            return await _experience.SummarizeAsync(new ExperienceQuery(agent.UserId, agent.Id, turn.Provider, turn.Model, turn.Role, agent.Timeframe, agent.MarketType), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Experience summary for agent {AgentId} role {Role} could not be read; the role runs without it", agent.Id, turn.Role);
            return null;
        }
    }

    private void Log(Turn turn, Guid agentId, Guid evaluationId)
    {
        switch (turn.Status)
        {
            case RoleOutputStatus.Succeeded:
                _logger.LogInformation("Council {Role} for agent {AgentId} evaluation {EvaluationId}: succeeded via {Provider} {Model} after {Attempts} attempt(s) in {LatencyMs} ms, tokens {InputTokens}/{OutputTokens}, proposes {Action} at confidence {Confidence}",
                    turn.Role, agentId, evaluationId, turn.Provider, turn.Model, turn.Attempts, (int)turn.Latency.TotalMilliseconds, turn.InputTokens, turn.OutputTokens, turn.Document!.ProposedAction, turn.Document.Confidence);
                break;
            case RoleOutputStatus.Disabled:
                _logger.LogDebug("Council {Role} for agent {AgentId} evaluation {EvaluationId}: disabled", turn.Role, agentId, evaluationId);
                break;
            default:
                _logger.LogWarning("Council {Role} for agent {AgentId} evaluation {EvaluationId}: {Status} via {Provider} {Model} after {Attempts} attempt(s) in {LatencyMs} ms, tokens {InputTokens}/{OutputTokens}: {Reason}",
                    turn.Role, agentId, evaluationId, turn.Status, turn.Provider, turn.Model, turn.Attempts, (int)turn.Latency.TotalMilliseconds, turn.InputTokens, turn.OutputTokens, turn.Error);
                break;
        }
    }

    private static JsonElement TranscriptOutput(Turn turn) => JsonSerializer.SerializeToElement(new
    {
        promptVersion = RolePrompts.PromptVersion,
        status = turn.Status.ToString(),
        attempts = turn.Attempts,
        finishReason = turn.FinishReason,
        reasoningTokens = turn.ReasoningTokens,
        document = turn.Document is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(turn.Document, turn.Document.GetType(), RolePrompts.DocumentJson),
        error = turn.Error,
        raw = turn.Raw,
    }, TranscriptJson);

    private static string Describe(AiFinishReason reason) => reason switch
    {
        AiFinishReason.MaxTokens => "the output token cap (the answer was cut off)",
        AiFinishReason.Refusal => "a refusal",
        AiFinishReason.ContentFiltered => "a content filter",
        _ => reason.ToString().ToLowerInvariant(),
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>One role's turn while the council runs; becomes a role output row and a transcript.</summary>
    private sealed class Turn(CouncilRole role, AiProvider provider, string model)
    {
        public CouncilRole Role { get; } = role;

        public AiProvider Provider { get; } = provider;

        public string Model { get; } = model;

        public RoleOutputStatus Status { get; set; } = RoleOutputStatus.Skipped;

        public string? Error { get; set; }

        public RoleOutputDocument? Document { get; set; }

        public string? Prompt { get; set; }

        public int Attempts { get; set; }

        public int InputTokens { get; set; }

        public int OutputTokens { get; set; }

        public int ReasoningTokens { get; set; }

        public TimeSpan Latency { get; set; }

        public string? FinishReason { get; set; }

        public string? Raw { get; set; }

        public string? Fault { get; set; }

        public bool DeadlinePassed { get; set; }
    }
}
