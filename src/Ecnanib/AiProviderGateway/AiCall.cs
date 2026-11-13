namespace Ecnanib.AiProviderGateway;

/// <summary>One structured call to a model.</summary>
/// <param name="Model">The provider's identifier for the model, as listed in the catalog.</param>
/// <param name="Prompt">
/// The prompt text. It is provider-neutral, and is sent as it is: an adapter adds nothing to it
/// (FR-PRM-10).
/// </param>
/// <param name="ResultSchema">The JSON Schema that the structured result must match: a JSON object.</param>
public sealed record AiCallRequest(string Model, string Prompt, string ResultSchema);

/// <summary>
/// How a call to a model ended. A call that gives no result is not an error of the gateway: it
/// is one of these, so that a caller, which may name no type of the network, can tell what to do
/// next. See docs/ai-providers.md.
/// </summary>
public enum AiCallOutcome
{
    /// <summary>The model finished its answer, and the answer is a JSON object.</summary>
    Succeeded,

    /// <summary>
    /// The provider answered, without a result that can be used: the model refused, the answer
    /// was cut short, the prompt was blocked, or the text is not a JSON object. The tokens were
    /// spent all the same, so the usage is given. Whether a result that is a JSON object matches
    /// its schema is not asked here: that is the caller's to check.
    /// </summary>
    InvalidResult,

    /// <summary>The call took longer than the provider's <c>TimeoutSeconds</c> of the configuration file.</summary>
    TimedOut,

    /// <summary>
    /// The provider answered as it answers a wrong key. It says that the key should be checked
    /// again, and not that the key is wrong: the check of the key decides that (FR-KEY-3).
    /// </summary>
    AuthenticationFailed,

    /// <summary>The provider's own error says that it has no such model (FR-CAT-8).</summary>
    ModelNotFound,

    /// <summary>
    /// Anything else: no connection, a limit on requests, a fault of the provider, a request that
    /// it would not take, or an answer that is not what the provider sends.
    /// </summary>
    Failed,
}

/// <summary>What a structured call returned.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="ResultJson">
/// The structured result, as the JSON object the model wrote. It is there when the call
/// succeeded, and never otherwise.
/// </param>
/// <param name="Usage">
/// The tokens the call used, or null when the provider did not say. Null is unknown, and not
/// none: a call that timed out may still be charged for.
/// </param>
/// <param name="Latency">How long the call took, from the request to the answer or the failure.</param>
public sealed record AiCallResult(AiCallOutcome Outcome, string? ResultJson, AiTokenUsage? Usage, TimeSpan Latency)
{
    public string? ResultJson { get; } = (Outcome == AiCallOutcome.Succeeded) == (ResultJson is not null)
        ? ResultJson
        : throw new ArgumentException("A call has a result when it succeeded, and only then.", nameof(ResultJson));
}

/// <summary>
/// Token usage of one call, counted the same way for every provider, so that it can be added up
/// and priced.
/// </summary>
/// <param name="InputTokens">Every token of the prompt, those the provider read from a cache included.</param>
/// <param name="OutputTokens">Every token the provider charges as output: the answer, and what the model reasoned before it.</param>
public readonly record struct AiTokenUsage(int InputTokens, int OutputTokens);
