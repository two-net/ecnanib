using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Configuration;

/// <summary>Validates the <c>Accounts</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class AccountsOptionsValidator : IValidateOptions<AccountsOptions>
{
    public ValidateOptionsResult Validate(string? name, AccountsOptions options)
    {
        var failures = new List<string>();
        var prefix = AccountsOptions.SectionName;

        RequirePositive($"{prefix}:SessionDays", options.SessionDays, failures);
        RequirePositive($"{prefix}:SessionRevalidationMinutes", options.SessionRevalidationMinutes, failures);
        RequirePositive($"{prefix}:SignInAttemptsPerWindow", options.SignInAttemptsPerWindow, failures);
        RequirePositive($"{prefix}:SignInAttemptsPerAddress", options.SignInAttemptsPerAddress, failures);
        RequirePositive($"{prefix}:SignInWindowMinutes", options.SignInWindowMinutes, failures);

        if (options.MinPasswordLength < 8)
            failures.Add($"{prefix}:MinPasswordLength must be >= 8.");
        if (options.MinPasswordLength > 64)
            failures.Add($"{prefix}:MinPasswordLength must be <= 64.");
        if (options.SignInAttemptsPerAddress < options.SignInAttemptsPerWindow)
            failures.Add($"{prefix}:SignInAttemptsPerAddress must be >= SignInAttemptsPerWindow.");
        if (options.SessionRevalidationMinutes > 0 && options.SessionDays > 0 && options.SessionRevalidation >= options.SessionLifetime)
            failures.Add($"{prefix}:SessionRevalidationMinutes must be shorter than the session.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(string path, int value, List<string> failures)
    {
        if (value <= 0)
            failures.Add($"{path} must be > 0.");
    }
}
