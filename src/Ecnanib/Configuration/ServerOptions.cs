using System.ComponentModel.DataAnnotations;
using System.Net;

namespace Ecnanib.Configuration;

/// <summary>
/// The <c>server</c> section of the system configuration file (Appendix D): where Kestrel listens and the origin
/// users reach the application at. Kestrel serves plain HTTP; TLS terminates at a reverse proxy, so the file holds no
/// certificate (FR-CFG-03). <see cref="PublicUrl"/> is that proxy's public origin and decides whether cookies are
/// marked Secure (NFR-SEC-02).
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>IP address to listen on: <c>127.0.0.1</c> behind a same-host proxy, <c>0.0.0.0</c> or <c>::</c> for every interface.</summary>
    [Required(AllowEmptyStrings = false)]
    public string BindAddress { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int Port { get; set; } = 5207;

    /// <summary>Absolute http or https origin without a path, e.g. <c>https://ecnanib.example.com</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string PublicUrl { get; set; } = "http://localhost:5207";

    /// <summary>The listen endpoint; valid only after validation, which is why it is not a property the annotations validator would read.</summary>
    public IPEndPoint ToEndPoint() => new(IPAddress.Parse(BindAddress), Port);

    public Uri? PublicUri => Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) ? uri : null;

    public bool IsHttps => string.Equals(PublicUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
