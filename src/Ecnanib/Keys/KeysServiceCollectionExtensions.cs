using Ecnanib.Keys.Abstractions;
using Ecnanib.Keys.Configuration;
using Ecnanib.Keys.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Keys;

public static class KeysServiceCollectionExtensions
{
    /// <summary>
    /// Registers the keys module: validated <see cref="KeysOptions"/>, <see cref="IKeyService"/>,
    /// <see cref="IKeyResolver"/>, the default <see cref="IKeyDependentAgents"/> (register the agents runtime's
    /// implementation first to replace it) and the <see cref="KeyRevalidator"/> hosted service (FR-KEY-07).
    /// Requires <c>AddDatabase</c>, <c>AddExchange</c>, <c>AddAiProviders</c> and <c>AddModelSync</c>. Connects to
    /// nothing at registration.
    /// </summary>
    public static IServiceCollection AddKeys(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<KeysOptions>()
            .Bind(configuration.GetSection(KeysOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<KeysOptions>, KeysOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<ExchangeKeyValidator>();
        services.TryAddSingleton<ProviderKeyValidator>();
        services.TryAddSingleton<IKeyDependentAgents, AgentKeyTransitions>();
        services.TryAddSingleton<StoredKeyRevalidation>();
        services.TryAddSingleton<IKeyService, KeyService>();
        services.TryAddSingleton<IKeyResolver, KeyResolver>();
        services.TryAddSingleton<KeyRevalidator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, KeyRevalidator>(sp => sp.GetRequiredService<KeyRevalidator>()));

        return services;
    }
}
