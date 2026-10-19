using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Configuration;

public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the validated <see cref="ServerOptions"/> and points Kestrel at <c>Server:BindAddress:Port</c> over
    /// plain HTTP. The section is validated with every other one before the host starts (FR-CFG-06), so an invalid
    /// address never reaches Kestrel.
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

        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<ServerOptions>>((kestrel, server) => kestrel.Listen(server.Value.ToEndPoint()));

        return services;
    }
}
