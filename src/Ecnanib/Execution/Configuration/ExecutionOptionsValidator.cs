using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Configuration;

/// <summary>Validates the <c>Execution</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class ExecutionOptionsValidator : IValidateOptions<ExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, ExecutionOptions options)
    {
        var failures = new List<string>();
        var prefix = ExecutionOptions.SectionName;

        if (options.ProtectiveOrderAttempts is < 1 or > 10)
            failures.Add($"{prefix}:ProtectiveOrderAttempts must be between 1 and 10.");
        if (options.ProtectiveOrderRetryDelayMs is < 100 or > 30_000)
            failures.Add($"{prefix}:ProtectiveOrderRetryDelayMs must be between 100 and 30000.");
        if (options.SpotStopLimitOffsetPercent is < 0.05m or > 5m)
            failures.Add($"{prefix}:SpotStopLimitOffsetPercent must be between 0.05 and 5.");
        if (options.ManageBudgetSeconds is < 5 or > 60)
            failures.Add($"{prefix}:ManageBudgetSeconds must be between 5 and 60.");
        if (options.ReconcileAfterSeconds is < 5 or > 600)
            failures.Add($"{prefix}:ReconcileAfterSeconds must be between 5 and 600.");
        if (options.FillTrackerStartDelaySeconds is < 0 or > 3600)
            failures.Add($"{prefix}:FillTrackerStartDelaySeconds must be between 0 and 3600.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
