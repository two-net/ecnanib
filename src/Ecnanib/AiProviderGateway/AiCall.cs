namespace Ecnanib.AiProviderGateway;

/// <summary>One structured call to a model.</summary>
/// <param name="Model">The provider's identifier for the model, as listed in the catalog.</param>
/// <param name="Prompt">The prompt text. It is provider-neutral.</param>
/// <param name="ResultSchema">The JSON Schema that the structured result must match.</param>
public sealed record AiCallRequest(string Model, string Prompt, string ResultSchema);

/// <summary>What a structured call returned.</summary>
/// <param name="ResultJson">The structured result, as JSON.</param>
/// <param name="Usage">The tokens the call used.</param>
/// <param name="Latency">How long the call took.</param>
public sealed record AiCallResult(string ResultJson, AiTokenUsage Usage, TimeSpan Latency);

/// <summary>Token usage of one call.</summary>
public readonly record struct AiTokenUsage(int InputTokens, int OutputTokens);
