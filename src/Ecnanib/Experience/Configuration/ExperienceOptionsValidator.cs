using Microsoft.Extensions.Options;

namespace Ecnanib.Experience.Configuration;

/// <summary>Validates the <c>Experience</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class ExperienceOptionsValidator : IValidateOptions<ExperienceOptions>
{
    public ValidateOptionsResult Validate(string? name, ExperienceOptions options)
    {
        var failures = new List<string>();
        var prefix = ExperienceOptions.SectionName;

        if (options.RefreshIntervalMinutes is < 1 or > 1440)
            failures.Add($"{prefix}:RefreshIntervalMinutes must be between 1 and 1440.");
        if (options.ShadowHorizonCandles is < 1 or > 200)
            failures.Add($"{prefix}:ShadowHorizonCandles must be between 1 and 200.");
        if (options.RecentOutcomes is < 1 or > 20)
            failures.Add($"{prefix}:RecentOutcomes must be between 1 and 20.");
        if (options.MinimumTradesForSuggestion is < 1 or > 1000)
            failures.Add($"{prefix}:MinimumTradesForSuggestion must be between 1 and 1000.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
