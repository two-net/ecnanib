using System.Net;

namespace Ecnanib.Platform.Contracts;

/// <summary>
/// How a client's address and user agent are stored, so that sessions and audit records show the
/// same text for the same client.
/// </summary>
public static class ClientText
{
    /// <summary>Long enough for an IPv6 address with a scope.</summary>
    public const int AddressMaxLength = 64;

    public const int UserAgentMaxLength = 512;

    /// <summary>A dual-stack socket reports IPv4 clients in mapped form, which nobody would recognise.</summary>
    public static string? Address(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString();

    /// <summary>
    /// A client writes this header, so it has to fit and be storable: PostgreSQL refuses a NUL, and half
    /// a surrogate pair can't be encoded. Either would be a 500 and a database row a visitor chose to write.
    /// </summary>
    public static string? UserAgent(string? userAgent)
    {
        var text = new string([.. (userAgent ?? string.Empty).Where(character => !char.IsControl(character))]).Trim();

        if (text.Length == 0)
        {
            return null;
        }

        var length = Math.Min(text.Length, UserAgentMaxLength);

        if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}
