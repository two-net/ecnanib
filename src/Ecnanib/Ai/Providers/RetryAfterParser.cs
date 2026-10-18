using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Ecnanib.Ai.Providers;

/// <summary>
/// Reads how long a provider asks us to wait: the <c>Retry-After</c> header (delta seconds or HTTP date), Gemini's
/// <c>RetryInfo.retryDelay</c> duration and OpenAI's "try again in …" message text. Results are never negative.
/// </summary>
internal static partial class RetryAfterParser
{
    public static TimeSpan? FromHeaders(HttpResponseHeaders? headers, DateTimeOffset now)
    {
        if (headers is null)
            return null;

        if (headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
                return Clamp(delta);
            if (retryAfter.Date is { } date)
                return Clamp(date - now);
        }

        if (headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var value in values)
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    return Clamp(TimeSpan.FromSeconds(seconds));
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    return Clamp(date - now);
            }
        }

        return null;
    }

    /// <summary>A protobuf duration in JSON form, for example <c>"33s"</c> or <c>"0.5s"</c>.</summary>
    public static TimeSpan? FromDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = DurationPattern().Match(value.Trim());
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Clamp(TimeSpan.FromSeconds(seconds))
            : null;
    }

    /// <summary>OpenAI's "Please try again in 20s" / "6m0s" / "250ms" phrasing.</summary>
    public static TimeSpan? FromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        var match = TryAgainPattern().Match(message);
        if (!match.Success)
            return null;

        var total = TimeSpan.Zero;
        foreach (Match part in UnitPattern().Matches(match.Groups[1].Value))
        {
            var amount = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            total += part.Groups[2].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.Zero,
            };
        }

        return total > TimeSpan.Zero ? total : null;
    }

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)s$")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"try again in ((?:\d+(?:\.\d+)?(?:ms|s|m|h))+)", RegexOptions.IgnoreCase)]
    private static partial Regex TryAgainPattern();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|s|m|h)")]
    private static partial Regex UnitPattern();
}
