using System.Text.Json;

namespace Ecnanib.Ai.Abstractions;

/// <summary>
/// One role prompt (FR-AI-06). Only these fields go on the wire: no temperature, thinking or other model-specific
/// parameters (FR-SYNC-05). <paramref name="OutputSchema"/> must be a JSON object that outlives its
/// <see cref="JsonDocument"/> (clone it); the module README lists the schema subset every provider accepts.
/// <paramref name="MaxOutputTokens"/> may lower the configured cap, never raise it (NFR-COST-01).
/// <paramref name="Purpose"/> (for example the council role) appears in the call log only (FR-LOG-04).
/// </summary>
public sealed record AiRequest(
    string Model,
    string SystemPrompt,
    string UserPrompt,
    JsonElement? OutputSchema = null,
    int? MaxOutputTokens = null,
    string? Purpose = null)
{
    public string Model { get; } = !string.IsNullOrWhiteSpace(Model)
        ? Model.Trim()
        : throw new ArgumentException("Model is required.", nameof(Model));

    public string SystemPrompt { get; } = SystemPrompt ?? throw new ArgumentNullException(nameof(SystemPrompt));

    public string UserPrompt { get; } = !string.IsNullOrWhiteSpace(UserPrompt)
        ? UserPrompt
        : throw new ArgumentException("User prompt is required.", nameof(UserPrompt));

    public JsonElement? OutputSchema { get; } = OutputSchema is { ValueKind: not JsonValueKind.Object }
        ? throw new ArgumentException("Output schema must be a JSON object.", nameof(OutputSchema))
        : OutputSchema;

    public int? MaxOutputTokens { get; } = MaxOutputTokens is <= 0
        ? throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens), MaxOutputTokens, "Max output tokens must be > 0.")
        : MaxOutputTokens;

    /// <summary>Never prints the prompts: a request object may end up in a log line (FR-LOG-04).</summary>
    public override string ToString() =>
        $"AiRequest({Model}, {(OutputSchema is null ? "text" : "json")}, {Purpose ?? "-"})";
}

/// <summary>Token usage as reported by the provider (AI-04). Subsets are null where a provider does not report them.</summary>
public sealed record AiTokenUsage(int InputTokens, int OutputTokens, int? CachedInputTokens, int? ReasoningTokens)
{
    public static AiTokenUsage None { get; } = new(0, 0, null, null);

    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// A provider response (AI-02). For a schema request <see cref="Text"/> is the raw JSON document when
/// <see cref="IsStructured"/>; the caller validates it against the schema (FR-AI-07). <see cref="Model"/> is the
/// identifier the provider reports (aliases resolve to it); <see cref="RawFinishReason"/> is the provider's literal.
/// </summary>
public sealed record AiCompletion(
    AiProvider Provider,
    string Model,
    string Text,
    bool IsStructured,
    AiFinishReason FinishReason,
    string? RawFinishReason,
    AiTokenUsage Usage,
    TimeSpan Latency,
    string? ResponseId);
