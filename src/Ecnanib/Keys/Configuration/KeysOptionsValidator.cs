using Microsoft.Extensions.Options;

namespace Ecnanib.Keys.Configuration;

/// <summary>Validates the <c>Keys</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class KeysOptionsValidator : IValidateOptions<KeysOptions>
{
    public ValidateOptionsResult Validate(string? name, KeysOptions options)
    {
        var failures = new List<string>();
        var prefix = KeysOptions.SectionName;

        if (options.RevalidationIntervalMinutes <= 0)
            failures.Add($"{prefix}:RevalidationIntervalMinutes must be > 0.");
        if (options.ValidationTimeoutSeconds <= 0)
            failures.Add($"{prefix}:ValidationTimeoutSeconds must be > 0.");
        if (options.RevalidationIntervalMinutes > 0 && options.ValidationTimeoutSeconds > 0 && options.ValidationTimeout >= options.RevalidationInterval)
            failures.Add($"{prefix}:ValidationTimeoutSeconds must be shorter than the revalidation interval.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
