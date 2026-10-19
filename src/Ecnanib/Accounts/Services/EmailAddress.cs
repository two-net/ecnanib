using System.Net.Mail;

namespace Ecnanib.Accounts.Services;

/// <summary>
/// Accepts an email address as entered and derives the unique lookup key (FR-ACC-01): trimmed, one <c>@</c> with
/// something on both sides, no whitespace, parseable as a bare address, at most 254 characters. The normalized form is
/// the upper-case invariant of the address, as <c>users.normalized_email</c> expects.
/// </summary>
internal static class EmailAddress
{
    public const int MaxLength = 254;

    public static bool TryNormalize(string? input, out string email, out string normalized)
    {
        email = normalized = string.Empty;
        if (input is null)
            return false;

        var trimmed = input.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
            return false;

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
            return false;

        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }

        // MailAddress accepts "Name <user@host>" and comments; the address must be the whole input.
        if (!MailAddress.TryCreate(trimmed, out var parsed) || !string.Equals(parsed.Address, trimmed, StringComparison.Ordinal))
            return false;

        email = trimmed;
        normalized = trimmed.ToUpperInvariant();
        return true;
    }
}
