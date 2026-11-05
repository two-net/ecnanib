using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.RiskEngine;

public static class RiskEngineModule
{
    public static IServiceCollection AddRiskEngine(this IServiceCollection services)
    {
        // One instance, so the engine, Agents and the Web UI apply the same caps.
        services.AddSingleton(provider => RiskCapsSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IRiskEngine>(provider => new Engine(provider.GetRequiredService<RiskCaps>()));
        services.AddHostedService<RiskEngineStartup>();

        return services;
    }
}
