using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Agents.Configuration;

/// <summary>Validates the runtime settings of the <c>Agents</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class AgentsOptionsValidator : IValidateOptions<AgentsOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentsOptions options)
    {
        var failures = new List<string>();
        var prefix = AgentsOptions.SectionName;

        var known = AgentsOptions.Timeframes.Select(AgentsOptions.KeyOf).ToList();
        foreach (var timeframe in AgentsOptions.Timeframes)
        {
            var key = AgentsOptions.KeyOf(timeframe);
            if (!options.EvaluationDeadlineSeconds.TryGetValue(key, out var seconds))
            {
                failures.Add($"{prefix}:EvaluationDeadlineSeconds:{key} must be set.");
                continue;
            }

            if (seconds <= 0)
                failures.Add($"{prefix}:EvaluationDeadlineSeconds:{key} must be > 0.");
            else if (TimeSpan.FromSeconds(seconds) >= timeframe.ToTimeSpan())
                failures.Add($"{prefix}:EvaluationDeadlineSeconds:{key} must be shorter than the timeframe.");
        }

        foreach (var key in options.EvaluationDeadlineSeconds.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            failures.Add($"{prefix}:EvaluationDeadlineSeconds:{key} is not a timeframe; use {string.Join(", ", known)}.");

        if (options.MaxConcurrentEvaluations is < 1 or > 1000)
            failures.Add($"{prefix}:MaxConcurrentEvaluations must be between 1 and 1000.");
        if (options.DefaultEntryTimeoutCandles is < 1 or > 100)
            failures.Add($"{prefix}:DefaultEntryTimeoutCandles must be between 1 and 100.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
