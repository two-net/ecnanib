using Ecnanib.Platform.Contracts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Platform;

public static class PlatformModule
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        services.AddDbContextFactory<EcnanibDbContext>((provider, options) =>
        {
            options.UsePostgres(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

            options.UseAsyncSeeding(async (context, _, cancellationToken) =>
            {
                var database = (EcnanibDbContext)context;

                foreach (var seeder in provider.GetServices<IReferenceDataSeeder>())
                {
                    await seeder.SeedAsync(database, cancellationToken);
                }

                await database.SaveChangesAsync(cancellationToken);
            });
        });

        // AddDbContextFactory also registers the context as scoped. A Blazor circuit keeps its scope
        // for as long as the user stays connected, so without this a context could live that long.
        services.RemoveAll<EcnanibDbContext>();

        // Registered first, so the database is ready before any later hosted service starts. The
        // web server's own hosted service is always added last.
        services.AddHostedService<DatabaseStartup>();

        return services;
    }
}
