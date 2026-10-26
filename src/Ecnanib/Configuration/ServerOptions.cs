using System.ComponentModel.DataAnnotations;
using System.Net;

namespace Ecnanib.Configuration;

/// <summary>
/// The <c>server</c> section (Appendix D): the origin users reach the application at and the reverse proxies whose
/// forwarded headers are believed. Where Kestrel listens is the framework's <c>Kestrel</c> section, plain HTTP by
/// default; TLS terminates at a reverse proxy, so no certificate is configured (FR-CFG-03). <see cref="PublicUrl"/> is
/// that proxy's public origin: https unless it is only reachable on this machine (NFR-SEC-02), and it decides whether
/// cookies are marked Secure, whether a plain request is redirected and whether HSTS is sent.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>Absolute https origin without a path, e.g. <c>https://ecnanib.example.com</c>; http is accepted for localhost and loopback addresses only.</summary>
    [Required(AllowEmptyStrings = false)]
    public string PublicUrl { get; set; } = "http://localhost:5207";

    /// <summary>
    /// Reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers are believed: IP addresses or
    /// networks in CIDR notation (<c>10.0.0.0/8</c>). Loopback is always trusted, so a proxy on the same host needs no entry.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    public Uri? PublicUri => Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) ? uri : null;

    public bool IsHttps => string.Equals(PublicUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when users reach the application on this machine only (localhost or a loopback address), where plain http is allowed.</summary>
    public bool IsLoopbackPublicHost => PublicUri?.IsLoopback == true;

    /// <summary>Parses one <see cref="TrustedProxies"/> entry: a single address, or a network whose address is its first.</summary>
    public static bool TryParseTrustedProxy(string? value, out IPAddress? address, out IPNetwork? network)
    {
        address = null;
        network = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (IPAddress.TryParse(text, out var parsedAddress))
        {
            address = parsedAddress;
            return true;
        }

        if (text.Contains('/') && IPNetwork.TryParse(text, out var parsedNetwork))
        {
            network = parsedNetwork;
            return true;
        }

        return false;
    }
}
