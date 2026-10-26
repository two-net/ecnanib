using Ecnanib.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Support;

/// <summary>
/// Reads the hosted-service registrations off a service collection without building it. Every module registers its
/// jobs as <c>ServiceDescriptor.Singleton&lt;IHostedService, X&gt;(sp =&gt; sp.GetRequiredService&lt;X&gt;())</c>, whose
/// factory delegate is a <c>Func&lt;IServiceProvider, X&gt;</c> and so names the implementation.
/// </summary>
internal static class HostedServices
{
    public static Type? ImplementationType(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? descriptor.ImplementationFactory?.GetType().GenericTypeArguments.ElementAtOrDefault(1);

    /// <summary>The application's own hosted services in registration order; the framework's (Data Protection, the server) are left out.</summary>
    public static IReadOnlyList<Type> OfApplication(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService)
            .Select(ImplementationType)
            .Where(t => t?.Assembly == typeof(EcnanibHost).Assembly)
            .Select(t => t!)
            .ToList();
}
