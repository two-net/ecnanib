using System.Net;
using System.Net.Sockets;

namespace Ecnanib.Accounts;

/// <summary>
/// Where a request came from: its IP address, as the connection knows it. The audit trail records
/// it with an action (NFR-LOG-7), and the limits on attempts count by it (NFR-SEC-4). It is
/// personal data. See docs/accounts.md.
/// </summary>
/// <remarks>
/// An origin is read from a connection and never made from text, so nothing else can be written
/// into the trail as one, and nothing a request says about itself decides whose attempts it
/// counts with. Behind a proxy it is the address of the browser only when the application is
/// told to believe the proxy's headers. See docs/security.md.
/// </remarks>
public sealed class AuditOrigin
{
    // One subscriber is given an IPv6 network of this many bits, and can use any address in it.
    private const int BytesOfAnIPv6Network = 8;

    private AuditOrigin(string? address, string client)
    {
        Address = address;
        Client = client;
    }

    /// <summary>For an action whose request is not at hand, or whose connection does not say where it came from.</summary>
    public static AuditOrigin Unknown { get; } = new(null, "unknown");

    /// <summary>The address as the trail stores it, or null when it is not known.</summary>
    internal string? Address { get; }

    /// <summary>
    /// Whose attempts the request counts with: those of its address, or of its network when the
    /// address is an IPv6 one. The requests whose address is not known count as one client.
    /// </summary>
    internal string Client { get; }

    /// <summary>The address a request came from.</summary>
    public static AuditOrigin Of(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var address = request.Connection.RemoteIpAddress;
        if (address is null)
        {
            return Unknown;
        }

        // An IPv4 address that arrived on an IPv6 socket is written as the IPv4 address it is.
        var known = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return new AuditOrigin(known.ToString(), ClientOf(known));
    }

    private static string ClientOf(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);

        return $"{Convert.ToHexString(bytes[..BytesOfAnIPv6Network])}/64";
    }
}
