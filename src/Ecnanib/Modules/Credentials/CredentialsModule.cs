using Ecnanib.Modules.Credentials.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.Credentials;

public static class CredentialsModule
{
    public static IServiceCollection AddCredentials(this IServiceCollection services)
    {
        services.AddSingleton(provider => CredentialSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddHostedService<CredentialsStartup>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AiValidator>();
        services.AddSingleton<BinanceValidator>();
        services.AddSingleton<ICredentials, Credentials>();
        services.AddSingleton<ICredentialKeys, CredentialKeys>();

        return services;
    }
}
