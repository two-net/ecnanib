using Microsoft.Extensions.Options;

namespace Ecnanib.Agents.Configuration;

/// <summary>Validates the <c>Paper</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class PaperOptionsValidator : IValidateOptions<PaperOptions>
{
    public ValidateOptionsResult Validate(string? name, PaperOptions options)
    {
        var failures = new List<string>();
        var prefix = PaperOptions.SectionName;

        if (options.StartingBalance <= 0)
            failures.Add($"{prefix}:StartingBalance must be > 0.");
        Percent(nameof(PaperOptions.SpotMakerFeePercent), options.SpotMakerFeePercent);
        Percent(nameof(PaperOptions.SpotTakerFeePercent), options.SpotTakerFeePercent);
        Percent(nameof(PaperOptions.FuturesMakerFeePercent), options.FuturesMakerFeePercent);
        Percent(nameof(PaperOptions.FuturesTakerFeePercent), options.FuturesTakerFeePercent);
        Percent(nameof(PaperOptions.SlippagePercent), options.SlippagePercent);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void Percent(string key, decimal value)
        {
            if (value < 0 || value > 10)
                failures.Add($"{prefix}:{key} must be between 0 and 10 (percent).");
        }
    }
}
