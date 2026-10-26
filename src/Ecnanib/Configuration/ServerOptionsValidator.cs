using System.Net;
using Microsoft.Extensions.Options;

namespace Ecnanib.Configuration;

/// <summary>Validates the <c>Server</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        var failures = new List<string>();
        var prefix = ServerOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.BindAddress) || !IPAddress.TryParse(options.BindAddress, out _))
            failures.Add($"{prefix}:BindAddress must be an IP address such as 127.0.0.1, ::1, 0.0.0.0 or :: (not a host name).");

        if (options.Port is < 1 or > 65535)
            failures.Add($"{prefix}:Port must be between 1 and 65535.");

        var uri = options.PublicUri;
        if (uri is null
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.UserInfo.Length > 0)
        {
            failures.Add($"{prefix}:PublicUrl must be an absolute http or https URL without path, query or fragment, e.g. https://ecnanib.example.com.");
        }
        else if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            // NFR-SEC-02: "production" is any origin other users can reach. Only this machine's own loopback stays http.
            failures.Add($"{prefix}:PublicUrl must be https unless its host is localhost or a loopback address: the application is served over HTTPS in production (NFR-SEC-02). Terminate TLS at a reverse proxy and set its https origin here.");
        }

        var proxies = options.TrustedProxies ?? [];
        for (var i = 0; i < proxies.Length; i++)
        {
            if (!ServerOptions.TryParseTrustedProxy(proxies[i], out _, out _))
                failures.Add($"{prefix}:TrustedProxies[{i}] must be an IP address or a network in CIDR notation whose address is the network's first, such as 10.0.0.0/8.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
