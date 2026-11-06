using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiCouncil.Contracts;

/// <summary>
/// Asks one role of the AI council (FR-ROLE-01): composes its prompt, calls its model on one of the user's own keys,
/// checks the answer against the role's contract, and moves to the fallback model when the model fails (FR-ROLE-06).
/// It spends the user's money, so BannedApis allows it only in AiCouncil and DecisionCycle. Every type it returns
/// is AiCouncil's or Kernel's, so its callers never see an adapter's. See Decision cycle in docs/architecture.md.
/// </summary>
public interface ICouncil
{
    /// <summary>
    /// The role's answer, or its failure, with every call it made on the way, billed or not, for the trace. A role's
    /// failure isn't an exception: the answer has no output. Prompts and answers are never logged (FR-LOG-07).
    /// </summary>
    /// <param name="cancellationToken">
    /// An AI call is safe to abandon, so a cycle passes <c>JobContext.Stopping</c> here.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    Task<RoleAnswer> AskAsync(RoleQuestion question, CancellationToken cancellationToken);
}

/// <summary>A model as the catalog names it. AiCouncil's own, because it can't use the agent's.</summary>
public sealed record CouncilModel(string Provider, string ModelId)
{
    public override string ToString() => $"{Provider} {ModelId}";
}

/// <summary>What to ask a role, and which models may answer.</summary>
/// <param name="Prompt">What the prompt is composed from, and the role.</param>
/// <param name="Fallback">The model to try when <paramref name="Model"/> fails (FR-ROLE-06). Optional.</param>
public sealed record RoleQuestion(Guid UserId, PromptRequest Prompt, CouncilModel Model, CouncilModel? Fallback = null)
{
    /// <summary>For the Risk Critic, the Strategist's validated proposal, which its changes must make less risky.</summary>
    public TradeProposal? Proposal { get; init; }

    public AiRole Role => Prompt.Role;

    /// <summary>Leaves the prompt out: custom instructions are the user's (FR-ADM-01, FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(RoleQuestion)} {Role} {Model}";
}

/// <summary>A role's answer, or its failure when <see cref="Output"/> is null, and every call it made, in order.</summary>
public sealed record RoleAnswer(AiRole Role, RoleOutput? Output, IReadOnlyList<CouncilCall> Calls)
{
    public bool Answered => Output is not null;

    /// <summary>The call that gave the output, or null when the role failed.</summary>
    public CouncilCall? AnsweredBy => Answered ? Calls.LastOrDefault(call => call.Status == CouncilCallStatus.Answered) : null;

    /// <summary>The model whose answer is the output, or the last one tried when the role failed.</summary>
    public CouncilModel? Model => (AnsweredBy ?? Calls.LastOrDefault())?.Model;

    public bool UsedFallback => (AnsweredBy ?? Calls.LastOrDefault())?.Fallback ?? false;

    public long InputTokens => Calls.Sum(call => call.InputTokens);

    public long OutputTokens => Calls.Sum(call => call.OutputTokens);

    /// <summary>Every known cost added up, or null when no call had a price, because a model without one has no cost.</summary>
    public decimal? Cost => Calls.Any(call => call.Cost is not null) ? Calls.Sum(call => call.Cost ?? 0m) : null;

    public override string ToString() => $"{nameof(RoleAnswer)} {Role} {(Answered ? "answered" : "failed")}";
}

/// <summary>
/// One call to a model, or one that couldn't be sent, for the decision trace (FR-UI-04). It holds the user's prompt
/// and the model's answer, so it's shown only to its user and never logged.
/// </summary>
/// <param name="Fallback">The role's fallback model made it.</param>
/// <param name="Attempt">1, or 2 for the retry an answer that broke its contract gets (FR-CYC-05).</param>
/// <param name="Failure">Why it failed or wasn't sent. Null when it answered, or only broke its contract.</param>
/// <param name="ProviderMessage">The provider's own error message, which can quote the prompt, so it's for the trace only.</param>
/// <param name="Instructions">Layers 1 to 4, or null when the prompt couldn't be composed.</param>
/// <param name="Message">
/// Layers 5 and 6, the first user message. A retry also sent the first answer and its violations after it.
/// </param>
/// <param name="Answer">The model's text, as it came.</param>
/// <param name="Output">The answer read and checked, when it conforms.</param>
/// <param name="Violations">What broke the contract, which a retry sent back.</param>
/// <param name="Cost">In US dollars, or null when the model has no price.</param>
/// <param name="Latency">Including the provider's retries and waits.</param>
/// <param name="ProviderAttempts">How many times it was sent, the adapter's retries included (EXT-AI-03).</param>
/// <param name="Templates">The prompt template versions it used (FR-PRM-03).</param>
public sealed record CouncilCall(
    AiRole Role,
    CouncilModel Model,
    bool Fallback,
    int Attempt,
    CouncilCallStatus Status,
    CouncilFailure? Failure,
    string? ProviderMessage,
    string? Instructions,
    string? Message,
    string? Answer,
    RoleOutput? Output,
    IReadOnlyList<ContractViolation> Violations,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    TimeSpan Latency,
    int ProviderAttempts,
    bool Truncated,
    IReadOnlyList<PromptTemplateVersion> Templates,
    DateTimeOffset StartedAt)
{
    /// <summary>Leaves out the prompt, the answer and the provider's message (FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(CouncilCall)} {Role} {Model} {Status}";
}

/// <summary>Stored by name, so a value is never renamed.</summary>
public enum CouncilCallStatus
{
    /// <summary>The answer conforms to the contract.</summary>
    Answered,

    /// <summary>The answer broke the contract (FR-PRM-08).</summary>
    BrokeContract,

    /// <summary>The provider failed, after its retries.</summary>
    Failed,

    /// <summary>Nothing was sent, so nothing was billed.</summary>
    NotSent,
}

/// <summary>Why a call failed or wasn't sent. Stored by name, so a value is never renamed.</summary>
public enum CouncilFailure
{
    /// <summary>None of the user's valid keys can use the model, as of the catalog's last sync.</summary>
    NoKey,

    /// <summary>The key stopped being usable since it was chosen, which its credential's checks deal with.</summary>
    KeyUnusable,

    /// <summary>A prompt layer has no template, which only an administrator can fix.</summary>
    TemplateMissing,

    Authentication,
    RateLimit,
    Overloaded,
    InvalidRequest,
    Refusal,
    Timeout,
    Other,
}
