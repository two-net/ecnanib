using Microsoft.Extensions.Options;

namespace Ecnanib.MarketData.Configuration;

/// <summary>Validates the <c>MarketData</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class MarketDataOptionsValidator : IValidateOptions<MarketDataOptions>
{
    public ValidateOptionsResult Validate(string? name, MarketDataOptions options)
    {
        var failures = new List<string>();
        var prefix = MarketDataOptions.SectionName;

        var known = MarketDataOptions.Intervals.Select(MarketDataOptions.KeyOf).ToList();
        foreach (var interval in MarketDataOptions.Intervals)
        {
            var key = MarketDataOptions.KeyOf(interval);
            if (!options.BackfillCandles.TryGetValue(key, out var count))
                failures.Add($"{prefix}:BackfillCandles:{key} must be set.");
            else if (count is < MarketDataOptions.MinBackfill or > MarketDataOptions.MaxBackfill)
                failures.Add($"{prefix}:BackfillCandles:{key} must be between {MarketDataOptions.MinBackfill} and {MarketDataOptions.MaxBackfill}.");
        }

        foreach (var key in options.BackfillCandles.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            failures.Add($"{prefix}:BackfillCandles:{key} is not a candle interval; use {string.Join(", ", known)}.");

        if (options.SymbolRefreshIntervalMinutes is < 1 or > 1440)
            failures.Add($"{prefix}:SymbolRefreshIntervalMinutes must be between 1 and 1440 (at least daily, FR-DATA-01).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
