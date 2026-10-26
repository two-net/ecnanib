using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Configuration;

public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>How long browsers remember to use https once they have seen the application over it (NFR-SEC-02).</summary>
    public static readonly TimeSpan HstsMaxAge = TimeSpan.FromDays(365);

    /// <summary>
    /// Registers the validated <see cref="ServerOptions"/> and configures what <see cref="EcnanibPipeline"/> runs behind
    /// the reverse proxy: which forwarded headers are read and from whom, and the HSTS policy. Where Kestrel listens is
    /// the framework's <c>Kestrel</c> section, read by Kestrel itself. The section is validated with every other one
    /// before the host starts (FR-CFG-06).
    /// </summary>
    public static IServiceCollection AddServer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ServerOptions>()
            .Bind(configuration.GetSection(ServerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>());

        // NFR-SEC-01/02: behind a trusted proxy the client address (sign-in throttle) and the scheme (Secure cookies,
        // HSTS, redirection) are the forwarded ones. The headers to read are named here, so ASPNETCORE_FORWARDEDHEADERS_ENABLED
        // is not needed; Host is not forwarded because absolute URLs come from Server:PublicUrl. Loopback is trusted by
        // default; Server:TrustedProxies adds a proxy on another machine.
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ServerOptions>>((forwarded, server) =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.ForwardLimit = 1;
                // A proxy that forwards the scheme but not the address, or the other way round, is still believed.
                forwarded.RequireHeaderSymmetry = false;
                foreach (var entry in server.Value.TrustedProxies ?? [])
                {
                    // Validation refuses an unparseable entry before the host starts; this only mirrors it.
                    if (!ServerOptions.TryParseTrustedProxy(entry, out var address, out var network))
                        continue;
                    if (address is not null)
                        forwarded.KnownProxies.Add(address);
                    else if (network is { } known)
                        forwarded.KnownIPNetworks.Add(known);
                }
            });

        // One year, this host only: the operator's domain may serve other things, and preloading is their decision.
        services.AddOptions<HstsOptions>()
            .Configure(hsts =>
            {
                hsts.MaxAge = HstsMaxAge;
                hsts.IncludeSubDomains = false;
                hsts.Preload = false;
            });

        return services;
    }
}
