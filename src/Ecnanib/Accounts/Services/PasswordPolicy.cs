namespace Ecnanib.Accounts.Services;

/// <summary>
/// Length is the only password rule (NIST SP 800-63B): at least <c>Accounts:MinPasswordLength</c> and at most
/// <see cref="MaxLength"/> characters, no composition requirements, nothing trimmed.
/// </summary>
internal static class PasswordPolicy
{
    public const int MaxLength = 128;

    public static bool IsAcceptable(string? password, int minLength) =>
        password is not null && password.Length >= minLength && password.Length <= MaxLength;
}
