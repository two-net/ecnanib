using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;
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
        services.AddSingleton<CredentialObservers>();
        services.AddSingleton<CredentialChecks>();
        services.AddSingleton<ICredentials, Credentials>();
        services.AddSingleton<ICredentialKeys, CredentialKeys>();
        services.AddSingleton<IRecurringJob, CredentialRevalidationJob>();

        return services;
    }
}
