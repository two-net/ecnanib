using System.Net.Mail;
using System.Text;

namespace Ecnanib.Modules.Identity;

internal static class EmailAddresses
{
    /// <summary>The longest address that SMTP can deliver to.</summary>
    public const int MaximumLength = 254;

    /// <summary>
    /// A bare address that everything after this can take. MailAddress alone isn't enough: it accepts a
    /// display name, as in "Sam &lt;a@b&gt;", and any non-ASCII character in the local part, including ones
    /// PostgreSQL rejects or <see cref="Normalize"/> throws on. Normalizing can also lengthen a string.
    /// </summary>
    public static bool IsValid(string email)
    {
        var trimmed = email.Trim();

        return trimmed.Length <= MaximumLength
            && !trimmed.Any(char.IsControl)
            && MailAddress.TryCreate(trimmed, out var address)
            && address.Address == trimmed
            && CanNormalize(trimmed);
    }

    /// <summary>
    /// What makes two spellings one account, and what Admin:AdminEmails is compared with. Without it,
    /// A@example.com could register alongside a@example.com, and a listed address could be imitated.
    /// Call <see cref="IsValid"/> first: this throws on some invalid input.
    /// </summary>
    public static string Normalize(string email) =>
        email.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private static bool CanNormalize(string email)
    {
        try
        {
            return Normalize(email).Length <= MaximumLength;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
