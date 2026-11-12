namespace Ecnanib.Accounts;

/// <summary>
/// Where an action in the audit trail came from: the IP address of the request, as the connection
/// knows it (NFR-LOG-7). It is personal data. See docs/accounts.md.
/// </summary>
/// <remarks>
/// An origin is read from a connection and never made from text, so nothing else can be written
/// into the trail as one. Behind a proxy it is the address of the browser only when the
/// application is told to believe the proxy's headers. See docs/security.md.
/// </remarks>
public sealed class AuditOrigin
{
    private AuditOrigin(string? address) => Address = address;

    /// <summary>For an action whose request is not at hand, or whose connection does not say where it came from.</summary>
    public static AuditOrigin Unknown { get; } = new(null);

    /// <summary>The address as the trail stores it, or null when it is not known.</summary>
    internal string? Address { get; }

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
        return new AuditOrigin((address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString());
    }
}
