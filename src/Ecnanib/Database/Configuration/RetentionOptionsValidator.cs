using Microsoft.Extensions.Options;

namespace Ecnanib.Database.Configuration;

/// <summary>Validates the <c>Retention</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class RetentionOptionsValidator : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        var failures = new List<string>();
        var prefix = RetentionOptions.SectionName;

        RequirePositive($"{prefix}:LogDays", options.LogDays, failures);
        RequirePositive($"{prefix}:CandleDays", options.CandleDays, failures);
        RequirePositive($"{prefix}:DeliberationTranscriptDays", options.DeliberationTranscriptDays, failures);
        RequirePositive($"{prefix}:SweepIntervalMinutes", options.SweepIntervalMinutes, failures);
        RequirePositive($"{prefix}:DeleteBatchSize", options.DeleteBatchSize, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(string path, int value, List<string> failures)
    {
        if (value <= 0)
            failures.Add($"{path} must be > 0.");
    }
}
