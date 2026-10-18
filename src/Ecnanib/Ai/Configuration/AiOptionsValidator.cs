using Ecnanib.Ai.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Configuration;

/// <summary>Validates the whole <c>Providers</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var failures = new List<string>();
        var prefix = AiOptions.SectionName;

        ValidateEndpoint($"{prefix}:{nameof(options.Claude)}", options.Claude, requireApiVersion: true, failures);
        ValidateEndpoint($"{prefix}:{nameof(options.Gemini)}", options.Gemini, requireApiVersion: false, failures);
        ValidateEndpoint($"{prefix}:{nameof(options.Gpt)}", options.Gpt, requireApiVersion: false, failures);
        ValidateEndpoint($"{prefix}:{nameof(options.Grok)}", options.Grok, requireApiVersion: false, failures);

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

    private static void ValidateEndpoint(string path, AiEndpointOptions? endpoint, bool requireApiVersion, List<string> failures)
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

        if (requireApiVersion && string.IsNullOrWhiteSpace(endpoint.ApiVersion))
            failures.Add($"{path}:ApiVersion is required.");
    }
}
