using Ecnanib.Platform.Contracts;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Extensions.Logging;

namespace Ecnanib.Platform;

public static class PlatformModule
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        AddStructuredLogging(services);

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

    /// <summary>Logs each HTTP request. Call it first, so it sees the final status.</summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingMiddleware>();

    /// <summary>
    /// Runs the application. An exception that escapes is printed raw by the runtime, and stderr
    /// usually ends up in the same place as the logs, so one carrying a secret is replaced by its
    /// masked form. Any other failure propagates untouched.
    /// </summary>
    public static void RunWithMaskedFailure(this WebApplication app)
    {
        // Read now, because the configuration is disposed with the host.
        var rules = SecretRules.From(app.Configuration);

        try
        {
            app.Run();
        }
        catch (Exception exception) when (rules.Scrub(exception.ToString()) is var masked && masked != exception.ToString())
        {
            throw new MaskedException(masked);
        }
    }

    private static void AddStructuredLogging(IServiceCollection services)
    {
        services.AddSingleton<LoggingPipeline>();

        // Keeps the framework's LoggerFactory rather than replacing it, so a provider injected by a
        // test still sees events. Every other provider goes, because it would see them unmasked.
        // Serilog decides levels, so the framework passes everything to it.
        services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<ILoggerProvider>(provider =>
            new SerilogLoggerProvider(provider.GetRequiredService<LoggingPipeline>().Logger));
    }
}
