using Ecnanib.Ai.Abstractions;
using Ecnanib.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Configuration;

/// <summary>Validates the whole <c>Providers</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    /// <summary>
    /// The domain each provider answers on (NFR-SEC-04): a configured base URL must be that host or one under it, so the
    /// file can pin a regional or versioned host but never send a key anywhere else (FR-KEY-05).
    /// </summary>
    internal static readonly IReadOnlyDictionary<AiProvider, string> AllowedDomains = new Dictionary<AiProvider, string>
    {
        [AiProvider.Claude] = "anthropic.com",
        [AiProvider.Gemini] = "googleapis.com",
        [AiProvider.Gpt] = "openai.com",
        [AiProvider.Grok] = "x.ai",
    };

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var failures = new List<string>();
        var prefix = AiOptions.SectionName;

        ValidateEndpoint($"{prefix}:{nameof(options.Claude)}", options.Claude, AiProvider.Claude, requireApiVersion: true, failures);
        ValidateEndpoint($"{prefix}:{nameof(options.Gemini)}", options.Gemini, AiProvider.Gemini, requireApiVersion: false, failures);
        ValidateEndpoint($"{prefix}:{nameof(options.Gpt)}", options.Gpt, AiProvider.Gpt, requireApiVersion: false, failures);
        ValidateEndpoint($"{prefix}:{nameof(options.Grok)}", options.Grok, AiProvider.Grok, requireApiVersion: false, failures);

        if (options.Retry is null)
        {
            failures.Add($"{prefix}:Retry is required.");
        }
        else
        {
            var r = options.Retry;
            var rp = $"{prefix}:Retry";
            if (r.MaxAttempts <= 0)
                failures.Add($"{rp}:MaxAttempts must be > 0.");
            if (r.BaseDelayMs <= 0)
                failures.Add($"{rp}:BaseDelayMs must be > 0.");
            if (r.MaxDelayMs < r.BaseDelayMs)
                failures.Add($"{rp}:MaxDelayMs must be >= BaseDelayMs.");
            if (r.MaxRetryAfterWaitSeconds < 0)
                failures.Add($"{rp}:MaxRetryAfterWaitSeconds must be >= 0.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateEndpoint(string path, AiEndpointOptions? endpoint, AiProvider provider, bool requireApiVersion, List<string> failures)
    {
        if (endpoint is null)
        {
            failures.Add($"{path} is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            failures.Add($"{path}:BaseUrl is required.");
        else if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri))
            failures.Add($"{path}:BaseUrl must be an absolute URL.");
        else if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            failures.Add($"{path}:BaseUrl must use the https scheme.");
        else if (!HostNames.IsUnder(uri.Host, AllowedDomains[provider]))
            failures.Add($"{path}:BaseUrl must be a host under {AllowedDomains[provider]}: outbound traffic is limited to the four providers (NFR-SEC-04).");

        if (requireApiVersion && string.IsNullOrWhiteSpace(endpoint.ApiVersion))
            failures.Add($"{path}:ApiVersion is required.");
    }
}
