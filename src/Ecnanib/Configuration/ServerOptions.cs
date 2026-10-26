using System.ComponentModel.DataAnnotations;
using System.Net;

namespace Ecnanib.Configuration;

/// <summary>
/// The <c>server</c> section of the system configuration file (Appendix D): where Kestrel listens, the origin users
/// reach the application at and the reverse proxies whose forwarded headers are believed. Kestrel serves plain HTTP;
/// TLS terminates at a reverse proxy, so the file holds no certificate (FR-CFG-03). <see cref="PublicUrl"/> is that
/// proxy's public origin: https unless it is only reachable on this machine (NFR-SEC-02), and it decides whether
/// cookies are marked Secure, whether a plain request is redirected and whether HSTS is sent.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>IP address to listen on: <c>127.0.0.1</c> behind a same-host proxy, <c>0.0.0.0</c> or <c>::</c> for every interface.</summary>
    [Required(AllowEmptyStrings = false)]
    public string BindAddress { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int Port { get; set; } = 5207;

    /// <summary>Absolute https origin without a path, e.g. <c>https://ecnanib.example.com</c>; http is accepted for localhost and loopback addresses only.</summary>
    [Required(AllowEmptyStrings = false)]
    public string PublicUrl { get; set; } = "http://localhost:5207";

    /// <summary>
    /// Reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers are believed: IP addresses or
    /// networks in CIDR notation (<c>10.0.0.0/8</c>). Loopback is always trusted, so a proxy on the same host needs no entry.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>The listen endpoint; valid only after validation, which is why it is not a property the annotations validator would read.</summary>
    public IPEndPoint ToEndPoint() => new(IPAddress.Parse(BindAddress), Port);

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
