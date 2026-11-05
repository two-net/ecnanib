using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// The settings credentials read, both optional. The server's egress addresses, which users put on a Binance
/// key's IP restriction (FR-KEY-05, SRS 7.2): each entry must be a single IPv4 or IPv6 address, because
/// that's what Binance's restriction takes. And how often every key is checked again (FR-KEY-11). A bad value
/// stops startup with a message naming it (CFG-03).
/// </summary>
internal sealed class CredentialSettings
{
    public const string EgressIpAddressesKey = "App:EgressIpAddresses";

    public const string RevalidationHoursKey = "Security:CredentialRevalidationHours";

    public const int DefaultRevalidationHours = 24;

    public const int MaximumRevalidationHours = 168;

    private CredentialSettings(IReadOnlyList<string> egressIpAddresses, TimeSpan revalidationInterval)
    {
        EgressIpAddresses = egressIpAddresses;
        RevalidationInterval = revalidationInterval;
    }

    /// <summary>In the form <see cref="IPAddress.ToString"/> writes them, in the order they're configured.</summary>
    public IReadOnlyList<string> EgressIpAddresses { get; }

    /// <summary>How often every key is checked with its provider again (FR-KEY-11).</summary>
    public TimeSpan RevalidationInterval { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static CredentialSettings Read(IConfiguration configuration) =>
        new(ReadEgressIpAddresses(configuration), TimeSpan.FromHours(ReadRevalidationHours(configuration)));

    private static int ReadRevalidationHours(IConfiguration configuration)
    {
        var value = configuration[RevalidationHoursKey];

        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultRevalidationHours;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
        {
            throw new InvalidOperationException($"{RevalidationHoursKey} must be a whole number.");
        }

        if (hours is < 1 or > MaximumRevalidationHours)
        {
            throw new InvalidOperationException($"{RevalidationHoursKey} must be between 1 and {MaximumRevalidationHours}.");
        }

        return hours;
    }

    private static List<string> ReadEgressIpAddresses(IConfiguration configuration)
    {
        var entries = configuration.GetSection(EgressIpAddressesKey).Get<string[]>() ?? [];
        var addresses = new List<string>(entries.Length);

        foreach (var (index, entry) in entries.Index())
        {
            var value = entry?.Trim();

            if (string.IsNullOrEmpty(value)
                || !IPAddress.TryParse(value, out var address)
                || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                || value.Contains('/', StringComparison.Ordinal)
                || value.Contains('%', StringComparison.Ordinal)
                || (address.AddressFamily == AddressFamily.InterNetwork && value.Count(character => character == '.') != 3))
            {
                throw new InvalidOperationException(
                    $"{EgressIpAddressesKey}:{index} must be one IP address, such as 203.0.113.10, with no range or port.");
            }

            var text = address.ToString();

            if (!addresses.Contains(text, StringComparer.Ordinal))
            {
                addresses.Add(text);
            }
        }

        return addresses;
    }
}
