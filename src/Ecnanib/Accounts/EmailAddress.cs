namespace Ecnanib.Accounts;

/// <summary>
/// The email address of an account, as registration, sign-in, and password reset all read it, so
/// that one address is one account however it is typed.
/// </summary>
internal static class EmailAddress
{
    // The longest address the mail protocol can carry.
    private const int Longest = 254;

    /// <summary>The address as it is stored: trimmed and in lower case.</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// One <c>@</c> with something before it and a domain with a dot after it, and no spaces or
    /// control characters. Whether the address exists is what verifying it finds out.
    /// </summary>
    public static bool LooksValid(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || email.Length > Longest)
        {
            return false;
        }

        var domain = email[(at + 1)..];

        return domain.Contains('.')
            && domain.Split('.').All(label => label.Length > 0)
            && !email.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    }
}
