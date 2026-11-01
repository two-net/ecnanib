using Ecnanib.Platform.Contracts;
using Ecnanib.Platform.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Brings the database up to date before the application serves anything (NFR-DEP-02, NFR-DEP-03).
/// A failure here propagates out of app.Run(), so the process exits without opening a port.
/// This is a hosted service because a top-level await in Program.cs would move the module
/// registrations out of the entry point, where Program_registers_every_module reads them.
/// </summary>
internal sealed class DatabaseStartup(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    LoggingPipeline logging,
    ILogger<DatabaseStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            // No limit: cutting off a long migration would fail the upgrade partway through.
            context.Database.SetCommandTimeout(0);

            await PrepareForMigrationHistoryAsync(context, cancellationToken);
            await RefuseSchemaNewerThanThisBuildAsync(context, cancellationToken);

            // Also runs the seeders, even with no migration pending. See PlatformModule.
            await context.Database.MigrateAsync(cancellationToken);

            logging.AttachDatabase();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogCritical(exception, "Database migration or seeding failed. The application will not start.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// MigrateAsync would create both of these, but the Npgsql provider reads the migration history
    /// first. That read survives a missing database or table, but EF logs the failure as an error,
    /// which would put errors in the log of a healthy first start.
    /// </summary>
    private static async Task PrepareForMigrationHistoryAsync(
        EcnanibDbContext context,
        CancellationToken cancellationToken)
    {
        var database = context.GetService<IRelationalDatabaseCreator>();

        if (!await database.ExistsAsync(cancellationToken))
        {
            await database.CreateAsync(cancellationToken);
        }

        await context.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
    }

    /// <summary>Migrations are forward-only (NFR-MNT-05), and EF silently ignores unknown history rows.</summary>
    private static async Task RefuseSchemaNewerThanThisBuildAsync(
        EcnanibDbContext context,
        CancellationToken cancellationToken)
    {
        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        var unknown = applied.Except(context.Database.GetMigrations()).ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"The database has migrations that this build doesn't include: {string.Join(", ", unknown)}. "
                + "Migrations are forward-only, so deploy a build that includes them.");
        }
    }
}
