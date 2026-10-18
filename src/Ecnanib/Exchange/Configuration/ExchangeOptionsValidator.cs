using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange.Configuration;

/// <summary>Validates the whole <c>Exchange</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class ExchangeOptionsValidator : IValidateOptions<ExchangeOptions>
{
    public ValidateOptionsResult Validate(string? name, ExchangeOptions options)
    {
        var failures = new List<string>();
        var prefix = ExchangeOptions.SectionName;

        if (options.Production is null)
            failures.Add($"{prefix}:Production is required.");
        else
            ValidateEndpoints($"{prefix}:Production", options.Production, failures);

        if (options.Testnet is null)
            failures.Add($"{prefix}:Testnet is required.");
        else
            ValidateEndpoints($"{prefix}:Testnet", options.Testnet, failures);

        if (options.Production is not null && options.Testnet is not null &&
            string.Equals(options.Production.SpotRest, options.Testnet.SpotRest, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{prefix}:Testnet:SpotRest must differ from {prefix}:Production:SpotRest.");
        }

        if (options.Streams is null)
        {
            failures.Add($"{prefix}:Streams is required.");
        }
        else
        {
            var s = options.Streams;
            var sp = $"{prefix}:Streams";
            RequirePositive(sp, nameof(s.NoDataTimeoutSeconds), s.NoDataTimeoutSeconds, failures);
            RequirePositive(sp, nameof(s.ReconnectInitialDelaySeconds), s.ReconnectInitialDelaySeconds, failures);
            RequirePositive(sp, nameof(s.ReconnectMaxDelaySeconds), s.ReconnectMaxDelaySeconds, failures);
            RequirePositive(sp, nameof(s.CandleCloseGraceSeconds), s.CandleCloseGraceSeconds, failures);
            RequirePositive(sp, nameof(s.LateCloseGraceSeconds), s.LateCloseGraceSeconds, failures);
            RequirePositive(sp, nameof(s.PricePollIntervalSeconds), s.PricePollIntervalSeconds, failures);
            RequirePositive(sp, nameof(s.MarkPricePollIntervalSeconds), s.MarkPricePollIntervalSeconds, failures);
            RequirePositive(sp, nameof(s.UserDataPollIntervalSeconds), s.UserDataPollIntervalSeconds, failures);
            if (s.ReconnectMaxDelaySeconds < s.ReconnectInitialDelaySeconds)
                failures.Add($"{sp}:ReconnectMaxDelaySeconds must be >= ReconnectInitialDelaySeconds.");
            if (s.MarkPriceUpdateIntervalMs is not (1000 or 3000))
                failures.Add($"{sp}:MarkPriceUpdateIntervalMs must be 1000 or 3000.");
        }

        if (options.Retry is null)
        {
            failures.Add($"{prefix}:Retry is required.");
        }
        else
        {
            var r = options.Retry;
            var rp = $"{prefix}:Retry";
            RequirePositive(rp, nameof(r.MaxAttempts), r.MaxAttempts, failures);
            RequirePositive(rp, nameof(r.BaseDelayMs), r.BaseDelayMs, failures);
            if (r.MaxDelayMs < r.BaseDelayMs)
                failures.Add($"{rp}:MaxDelayMs must be >= BaseDelayMs.");
            if (r.MaxRetryAfterWaitSeconds < 0)
                failures.Add($"{rp}:MaxRetryAfterWaitSeconds must be >= 0.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateEndpoints(string path, ExchangeEndpointOptions e, List<string> failures)
    {
        RequireUri(path, nameof(e.SpotRest), e.SpotRest, "https", failures);
        RequireUri(path, nameof(e.SpotStreams), e.SpotStreams, "wss", failures);
        RequireUri(path, nameof(e.SpotWsApi), e.SpotWsApi, "wss", failures);
        RequireUri(path, nameof(e.FuturesRest), e.FuturesRest, "https", failures);
        RequireUri(path, nameof(e.FuturesStreams), e.FuturesStreams, "wss", failures);
        if (!string.IsNullOrWhiteSpace(e.FuturesWsApi))
            RequireUri(path, nameof(e.FuturesWsApi), e.FuturesWsApi, "wss", failures);
    }

    private static void RequireUri(string path, string name, string? value, string scheme, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{path}:{name} is required.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            failures.Add($"{path}:{name} must be an absolute URL.");
            return;
        }

        if (!string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            failures.Add($"{path}:{name} must use the {scheme} scheme.");
    }

    private static void RequirePositive(string path, string name, int value, List<string> failures)
    {
        if (value <= 0)
            failures.Add($"{path}:{name} must be > 0.");
    }
}
