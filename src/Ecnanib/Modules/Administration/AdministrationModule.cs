using Ecnanib.Modules.Administration.Contracts;

namespace Ecnanib.Modules.Administration;

public static class AdministrationModule
{
    public static IServiceCollection AddAdministration(this IServiceCollection services)
    {
        services.AddSingleton<ISystemInfo, SystemInfo>();
        return services;
    }
}
