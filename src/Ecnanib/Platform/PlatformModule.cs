using Ecnanib.Platform.Audit;
using Ecnanib.Platform.Contracts;
using Ecnanib.Platform.Correlation;
using Ecnanib.Platform.Email;
using Ecnanib.Platform.Jobs;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Secrets;
using Ecnanib.Platform.Secrets.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Extensions.Logging;

namespace Ecnanib.Platform;

public static class PlatformModule
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        AddStructuredLogging(services);

        services.AddSingleton<ICorrelationContext, CorrelationContext>();
        services.AddScoped<CircuitHandler, CorrelationCircuitHandler>();

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

        // It migrates in StartingAsync, which runs before any hosted service's StartAsync, including
        // the framework's, which the Razor components register earlier and which reads the database.
        services.AddHostedService<DatabaseStartup>();

        AddSecrets(services);
        AddJobs(services);
        AddAudit(services);
        AddEmail(services);

        services.AddSingleton<DatabaseHealthCheck>();
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

        return services;
    }

    /// <summary>
    /// Gives each HTTP request a correlation ID. Call it first: request logging needs the ID, and
    /// anything after the exception handler or status pages runs twice when they re-execute.
    /// </summary>
    public static IApplicationBuilder UseCorrelation(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationMiddleware>();

    /// <summary>Logs each HTTP request. Call it straight after UseCorrelation, so it sees the final status.</summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingMiddleware>();

    /// <summary>
    /// Records each HTTP request's address and user agent for the audit trail. Call it after
    /// UseForwardedHeaders, which is what makes the address the client's, and outside the exception
    /// handler, because a re-executed request comes from the same client.
    /// </summary>
    public static IApplicationBuilder UseClientOrigin(this IApplicationBuilder app) =>
        app.UseMiddleware<ClientOriginMiddleware>();

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

    /// <summary>
    /// SecretStartup is registered straight after DatabaseStartup, because its StartingAsync needs the
    /// migrated database. Reading the keys checks the configuration file's permissions first (NFR-SEC-05).
    /// </summary>
    private static void AddSecrets(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            ConfigurationFileAccess.Verify(configuration, provider.GetRequiredService<IHostEnvironment>());

            return MasterKeys.Read(configuration);
        });

        services.AddSingleton<SecretStore>();
        services.AddSingleton<ISecretStore>(provider => provider.GetRequiredService<SecretStore>());
        services.AddSingleton<IMasterKeyRotation, MasterKeyRotation>();
        services.AddSingleton<SecretReencryption>();
        services.AddSingleton<IRecurringJob, SecretReencryptionJob>();
        services.AddHostedService<SecretStartup>();

        // The key ring lives in the database, and each key's secret part in secrets, under the master
        // key. A fixed name, because the default comes from the content root path.
        services.AddDataProtection().SetApplicationName("Ecnanib");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IDbContextFactory<EcnanibDbContext>, SecretStore>((options, contextFactory, secrets) =>
            {
                options.XmlRepository = new DatabaseXmlRepository(contextFactory);
                options.XmlEncryptor = new SecretStoreXmlEncryptor(secrets);
            });
    }

    /// <summary>The runner reads and writes job_runs in StartAsync, after DatabaseStartup has migrated.</summary>
    private static void AddJobs(IServiceCollection services)
    {
        services.Configure<HostOptions>(options => options.ShutdownTimeout = JobRunner.ShutdownTimeout);

        services.AddSingleton<JobRunStore>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<IJobRunner>(provider => provider.GetRequiredService<JobRunner>());
        services.AddHostedService(provider => provider.GetRequiredService<JobRunner>());
    }

    /// <summary>
    /// The hub filter is added here, before any module's, so it runs outside them and the client is
    /// known by the time their filters and the circuit's work run.
    /// </summary>
    private static void AddAudit(IServiceCollection services)
    {
        services.AddSingleton<ClientOriginContext>();
        services.AddSingleton<IAuditTrail, AuditTrail>();
        services.AddSingleton<ClientOriginHubFilter>();
        services.Configure<HubOptions>(options => options.AddFilter<ClientOriginHubFilter>());
    }

    /// <summary>
    /// Registered whether or not email is on, so that callers can depend on it. EmailStartup takes the
    /// settings, which is what validates them before any migration runs.
    /// </summary>
    private static void AddEmail(IServiceCollection services)
    {
        services.AddSingleton(provider => EmailSettings.Read(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IHostEnvironment>()));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddHostedService<EmailStartup>();
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
