using System.ComponentModel.DataAnnotations;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Ai.Configuration;

/// <summary>
/// The <c>providers</c> section of the system configuration file (Appendix D): base endpoints per provider, model sync
/// interval, consecutive-miss threshold for deprecation, call timeout and max output tokens. Contains no credentials
/// (FR-CFG-03). The sync interval and miss threshold are read by the model-sync module (FR-SYNC-02, FR-SYNC-03).
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Providers";

    /// <summary>Timeout of one HTTP attempt (FR-AI-13). The caller's cancellation token is the overall deadline.</summary>
    [Range(1, 600)]
    public int CallTimeoutSeconds { get; set; } = 60;

    /// <summary>Cap on output tokens per call (NFR-COST-01). Reasoning tokens count against it on GPT, Grok and Gemini.</summary>
    [Range(1, 1_000_000)]
    public int MaxOutputTokens { get; set; } = 8192;

    [Range(1, 10080)]
    public int ModelSyncIntervalMinutes { get; set; } = 360;

    /// <summary>A model absent from this many consecutive syncs is marked deprecated (FR-SYNC-03).</summary>
    [Range(1, 100)]
    public int DeprecationMissThreshold { get; set; } = 3;

    public AiEndpointOptions Claude { get; set; } = new() { BaseUrl = "https://api.anthropic.com/v1", ApiVersion = "2023-06-01" };

    public AiEndpointOptions Gemini { get; set; } = new() { BaseUrl = "https://generativelanguage.googleapis.com/v1beta" };

    public AiEndpointOptions Gpt { get; set; } = new() { BaseUrl = "https://api.openai.com/v1" };

    public AiEndpointOptions Grok { get; set; } = new() { BaseUrl = "https://api.x.ai/v1" };

    public AiRetryOptions Retry { get; set; } = new();

    public TimeSpan CallTimeout => TimeSpan.FromSeconds(CallTimeoutSeconds);

    public TimeSpan ModelSyncInterval => TimeSpan.FromMinutes(ModelSyncIntervalMinutes);

    public AiEndpointOptions EndpointsFor(AiProvider provider) => provider switch
    {
        AiProvider.Claude => Claude,
        AiProvider.Gemini => Gemini,
        AiProvider.Gpt => Gpt,
        AiProvider.Grok => Grok,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider."),
    };
}

/// <summary>
/// One provider's endpoint. <see cref="BaseUrl"/> includes the API version path so operators can pin it; clients
/// append only the resource path.
/// </summary>
public sealed class AiEndpointOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Claude only: sent as the <c>anthropic-version</c> header. The other providers version through the path.</summary>
    public string? ApiVersion { get; set; }
}

public sealed class AiRetryOptions
{
    public int MaxAttempts { get; set; } = 3;

    public int BaseDelayMs { get; set; } = 500;

    public int MaxDelayMs { get; set; } = 10000;

    /// <summary>A Retry-After longer than this is not waited for; the rate-limit exception is surfaced instead.</summary>
    public int MaxRetryAfterWaitSeconds { get; set; } = 30;

    public TimeSpan BaseDelay => TimeSpan.FromMilliseconds(BaseDelayMs);

    public TimeSpan MaxDelay => TimeSpan.FromMilliseconds(MaxDelayMs);

    public TimeSpan MaxRetryAfterWait => TimeSpan.FromSeconds(MaxRetryAfterWaitSeconds);
}
