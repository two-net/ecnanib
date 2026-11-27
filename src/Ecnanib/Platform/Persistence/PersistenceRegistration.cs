using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Ecnanib.Platform.Persistence;

/// <summary>Registers the database.</summary>
internal static class PersistenceRegistration
{
    /// <summary>
    /// Registers the one PostgreSQL database behind <see cref="EcnanibDbContext"/>, with the
    /// connection from the configuration file (NFR-DAT-1). A component takes
    /// <see cref="IDbContextFactory{TContext}"/> and creates a context for each piece of work.
    /// </summary>
    /// <remarks>
    /// Nothing connects here. The first connection is opened when the application migrates the
    /// database at startup.
    /// </remarks>
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        // One pool of connections for the application, closed when the host is disposed.
        services.AddSingleton(provider =>
            NpgsqlDataSource.Create(provider.GetRequiredService<ConnectionStringsSettings>().EcnanibDbContext.Reveal()));

        services.AddDbContextFactory<EcnanibDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<DatabaseMigrator>();

        // The backups of the database, the test of a restore, and the jobs that take the one and
        // run the other (NFR-DAT-11). The migrations ask for a backup too, before one of them
        // that is marked as destructive. See docs/backups.md.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PostgresTools>();
        services.AddSingleton<DatabaseBackups>();
        services.AddSingleton<RestoreTests>();
        services.AddBackgroundJob<DatabaseBackupJob>();
        services.AddBackgroundJob<RestoreTestJob>();

        // The sizes of the tables, for the operator status screen (FR-OPS-2).
        services.AddSingleton<IDatabaseSizes, DatabaseSizes>();

        return services;
    }
}

/// <summary>
/// Gives the tool that generates migrations the context without starting the application, so
/// generating one needs neither a configuration file nor a database.
/// </summary>
internal sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<EcnanibDbContext>
{
    public EcnanibDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<EcnanibDbContext>().UseNpgsql().Options);
}
