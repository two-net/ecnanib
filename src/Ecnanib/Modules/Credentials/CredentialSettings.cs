using System.Net;
using System.Net.Sockets;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// The one setting credentials read: the server's egress addresses, which users put on a Binance key's IP
/// restriction (FR-KEY-05, SRS 7.2). Optional. Each entry must be a single IPv4 or IPv6 address, because
/// that's what Binance's restriction takes, and a bad one stops startup with a message naming it (CFG-03).
/// </summary>
internal sealed class CredentialSettings
{
    public const string EgressIpAddressesKey = "App:EgressIpAddresses";

    private CredentialSettings(IReadOnlyList<string> egressIpAddresses) => EgressIpAddresses = egressIpAddresses;

    /// <summary>In the form <see cref="IPAddress.ToString"/> writes them, in the order they're configured.</summary>
    public IReadOnlyList<string> EgressIpAddresses { get; }

    /// <exception cref="InvalidOperationException">An entry isn't an IP address. The message names it.</exception>
    public static CredentialSettings Read(IConfiguration configuration)
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

        return new CredentialSettings(addresses);
    }
}
