using Ecnanib.Database.Configuration;
using Ecnanib.Database.Migrations;
using Ecnanib.Database.Retention;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Database;

public static class DatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database module: validated <see cref="DatabaseOptions"/> and <see cref="RetentionOptions"/>,
    /// the <see cref="EcnanibDbContext"/> factory (plus a scoped context), the Data Protection key ring persisted
    /// in the database (FR-CFG-05), the startup migrator, the retention sweeper and the instance lock that keeps the
    /// database to one process (NFR-REL-04, also <see cref="IInstanceLockStatus"/>). Nothing connects until
    /// <see cref="MigrateDatabaseAsync"/> runs.
    /// </summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());

        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RetentionOptions>, RetentionOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContextFactory<EcnanibDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            NpgsqlConfiguration.Apply(builder, options.ConnectionString, options);
        });
        services.AddDataProtection().PersistKeysToDbContext<EcnanibDbContext>();

        services.TryAddSingleton<DatabaseMigrator>();
        services.TryAddSingleton<InstanceLock>();
        services.TryAddSingleton<IInstanceLockStatus>(sp => sp.GetRequiredService<InstanceLock>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, InstanceLock>(sp => sp.GetRequiredService<InstanceLock>()));
        services.TryAddSingleton<RetentionSweeper>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RetentionSweeper>(sp => sp.GetRequiredService<RetentionSweeper>()));

        return services;
    }

    /// <summary>
    /// Takes the database's instance lock (NFR-REL-04) after <see cref="MigrateDatabaseAsync"/> and before the host
    /// starts, so no hosted service of a second instance ever runs against the same database. Waits
    /// <c>Database:InstanceLockWaitSeconds</c> for a previous instance to release it, then throws
    /// <see cref="InstanceLockException"/> after logging the cause at Critical; <c>Program</c> exits with code 1.
    /// </summary>
    public static Task AcquireInstanceLockAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Services.GetRequiredService<InstanceLock>().AcquireAsync(cancellationToken);
    }

    /// <summary>
    /// Applies pending migrations before the host starts (DB-02, NFR-ARCH-03). Call it right after building the
    /// application and before running it; it throws <see cref="DatabaseMigrationException"/> (or an
    /// <see cref="AggregateException"/> of <see cref="OptionsValidationException"/> for an invalid section) after
    /// logging the cause at Critical, and <c>Program</c> exits with code 1 (FR-CFG-06).
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        DatabaseMigrator migrator;
        try
        {
            // FR-CFG-06: every section registered with ValidateOnStart is checked here, before any DDL runs, rather
            // than when the host starts (which would be after the forward-only migrations were applied).
            host.Services.GetService<IStartupValidator>()?.Validate();
            migrator = host.Services.GetRequiredService<DatabaseMigrator>();
        }
        catch (Exception ex) when (ex is OptionsValidationException or AggregateException)
        {
            var failures = ex is AggregateException aggregate
                ? aggregate.InnerExceptions.OfType<OptionsValidationException>().SelectMany(e => e.Failures)
                : ((OptionsValidationException)ex).Failures;
            host.Services.GetRequiredService<ILogger<DatabaseMigrator>>()
                .LogCritical("Configuration is invalid; the application will not start: {Failures}", string.Join(" ", failures));
            throw;
        }

        await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
