using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
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
