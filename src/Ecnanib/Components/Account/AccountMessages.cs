using Ecnanib.Accounts.Services;

namespace Ecnanib.Components.Account;

/// <summary>User-facing texts shared by the account pages.</summary>
internal static class AccountMessages
{
    public const string IncorrectCredentials = "Incorrect email or password.";

    public static string TooManyAttempts(TimeSpan? retryAfter)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((retryAfter ?? TimeSpan.Zero).TotalMinutes));
        return $"Too many attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
    }

    public static string PasswordRule(int minLength) =>
        $"The password must be between {minLength} and {PasswordPolicy.MaxLength} characters.";
}
