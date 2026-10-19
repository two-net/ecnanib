using Microsoft.Extensions.Options;

namespace Ecnanib.Agents.Configuration;

/// <summary>Validates the <c>RiskCaps</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class RiskCapsOptionsValidator : IValidateOptions<RiskCapsOptions>
{
    public ValidateOptionsResult Validate(string? name, RiskCapsOptions options)
    {
        var failures = new List<string>();
        var prefix = RiskCapsOptions.SectionName;

        if (options.MaxRiskPerTradePercent <= 0 || options.MaxRiskPerTradePercent > 100)
            failures.Add($"{prefix}:MaxRiskPerTradePercent must be > 0 and <= 100.");
        if (options.MaxLeverage is < 1 or > 125)
            failures.Add($"{prefix}:MaxLeverage must be between 1 and 125.");
        if (options.MaxDailyLossPercent <= 0 || options.MaxDailyLossPercent > 100)
            failures.Add($"{prefix}:MaxDailyLossPercent must be > 0 and <= 100.");
        if (options.MaxOpenPositions is < 1 or > 100)
            failures.Add($"{prefix}:MaxOpenPositions must be between 1 and 100.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
