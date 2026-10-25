using Microsoft.Extensions.Options;

namespace Ecnanib.Ui.Configuration;

/// <summary>Validates the <c>Ui</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class UiOptionsValidator : IValidateOptions<UiOptions>
{
    public ValidateOptionsResult Validate(string? name, UiOptions options)
    {
        var failures = new List<string>();
        var prefix = UiOptions.SectionName;

        if (UiOptions.TryParseTheme(options.DefaultTheme) is null)
            failures.Add($"{prefix}:DefaultTheme must be System, Dark or Light.");

        if (options.TransitionMilliseconds is < 1 or > 400)
            failures.Add($"{prefix}:TransitionMilliseconds must be between 1 and 400 (FR-UI-03).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
