namespace Ecnanib.Ai.Abstractions;

/// <summary>AI providers the council can use (C-04, AI-01). One key per provider per user (FR-KEY-03).</summary>
public enum AiProvider
{
    Claude = 1,
    Gemini = 2,
    Gpt = 3,
    Grok = 4,
}

/// <summary>
/// Why generation ended. Refusals, safety blocks and truncation are results, not exceptions, so the call's usage is
/// still reported (AI-04). Only <see cref="Stop"/> means the text is complete.
/// </summary>
public enum AiFinishReason
{
    Unknown = 0,
    /// <summary>The model finished naturally; for a schema request the text is the complete JSON document.</summary>
    Stop = 1,
    /// <summary>The output-token cap was reached; the text may be truncated.</summary>
    MaxTokens = 2,
    /// <summary>The model declined to answer.</summary>
    Refusal = 3,
    /// <summary>The provider's safety filter blocked the prompt or the output.</summary>
    ContentFiltered = 4,
    Other = 99,
}

public enum AiErrorKind
{
    Unknown = 0,
    Network = 1,
    Timeout = 2,
    Unauthorized = 3,
    RateLimited = 4,
    /// <summary>Billing or quota state the provider will not serve; waiting does not help.</summary>
    QuotaExceeded = 5,
    /// <summary>The provider rejected the model identifier (FR-SYNC-04).</summary>
    UnknownModel = 6,
    InvalidRequest = 7,
    ContextTooLong = 8,
    /// <summary>A 2xx response that could not be read as the provider's documented shape.</summary>
    InvalidResponse = 9,
    Overloaded = 10,
    ServerError = 11,
}
