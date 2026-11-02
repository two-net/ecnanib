using System.Net;
using System.Net.Sockets;

namespace Ecnanib.Endpoints.Security;

/// <summary>
/// The settings the web security baseline reads. Read() is the only way in, and it reads these keys
/// and no others, because the App and Security sections also hold keys that other issues own (CFG-03).
/// </summary>
internal sealed class WebSecuritySettings
{
    private const string PublicBaseUrlKey = "App:PublicBaseUrl";
    private const string TrustedProxiesKey = "Security:TrustedProxies";
    private const string RateLimitsKey = "Security:RateLimits";

    private WebSecuritySettings(Uri? publicBaseUrl, IReadOnlyList<IPNetwork> trustedProxies, RateLimitSettings rateLimits)
    {
        PublicOrigin = publicBaseUrl?.GetLeftPart(UriPartial.Authority);
        HttpsPort = publicBaseUrl?.Scheme == Uri.UriSchemeHttps ? publicBaseUrl.Port : null;
        TrustedProxies = trustedProxies;
        RateLimits = rateLimits;
    }

    /// <summary>The origin a browser reports for this application. Null only in Development.</summary>
    public string? PublicOrigin { get; }

    /// <summary>Where plain HTTP redirects to. Without a value the framework quietly serves plain HTTP.</summary>
    public int? HttpsPort { get; }

    /// <summary>Proxies trusted on top of the framework's default of loopback.</summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; }

    public RateLimitSettings RateLimits { get; }

    /// <exception cref="InvalidOperationException">A setting is missing or invalid. The message names it.</exception>
    public static WebSecuritySettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var proxies = configuration.GetSection(TrustedProxiesKey).Get<string[]>() ?? [];

        // Strict because this sub-section is ours alone, so a typo must not pass for a setting.
        var rateLimits = configuration.GetSection(RateLimitsKey)
            .Get<RateLimitSettings>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new RateLimitSettings();

        Require(rateLimits.RequestsPerMinute >= 1, $"{RateLimitsKey}:RequestsPerMinute must be at least 1.");
        Require(
            rateLimits.AuthenticationRequestsPerMinute >= 1,
            $"{RateLimitsKey}:AuthenticationRequestsPerMinute must be at least 1.");

        return new WebSecuritySettings(
            ReadPublicBaseUrl(configuration, environment),
            [.. proxies.Select(ParseProxy)],
            rateLimits);
    }

    private static Uri? ReadPublicBaseUrl(IConfiguration configuration, IHostEnvironment environment)
    {
        var value = configuration[PublicBaseUrlKey];

        if (string.IsNullOrWhiteSpace(value))
        {
            Require(environment.IsDevelopment(), $"{PublicBaseUrlKey} is required outside Development.");
            return null;
        }

        Require(
            Uri.TryCreate(value, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp),
            $"{PublicBaseUrlKey} must be an absolute http or https URL.");
        Require(
            environment.IsDevelopment() || url!.Scheme == Uri.UriSchemeHttps,
            $"{PublicBaseUrlKey} must use https outside Development.");

        return url;
    }

    private static IPNetwork ParseProxy(string value, int index)
    {
        if (IPAddress.TryParse(value, out var address))
        {
            return new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        }

        Require(
            IPNetwork.TryParse(value, out var network),
            $"{TrustedProxiesKey}:{index} must be an IP address or a network in CIDR notation.");

        return network;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

/// <summary>Optional, because a limit nobody configured is still safer than no limit.</summary>
internal sealed class RateLimitSettings
{
    /// <summary>Applies per IP address and, separately, per account.</summary>
    public int RequestsPerMinute { get; init; } = 300;

    /// <summary>Applies per IP address, on top of RequestsPerMinute.</summary>
    public int AuthenticationRequestsPerMinute { get; init; } = 10;
}
