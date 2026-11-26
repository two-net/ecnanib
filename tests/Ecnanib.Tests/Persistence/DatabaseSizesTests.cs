using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// The sizes of the tables, as an operator status screen shows them (FR-OPS-2): decision records
/// and transcripts grow with every cycle, and this is how that growth is watched (risk R7).
/// </summary>
public class DatabaseSizesTests
{
    [Fact]
    public async Task Every_table_of_the_database_is_listed_once_with_a_size_the_largest_first()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        var sizes = await ReadAsync(application);

        // The tables of the model, and the one in which the migrations are kept: whatever the
        // database has is listed, so a table that nobody thought of shows too.
        using var context = database.Context();
        var expected = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName()!)
            .Append("__EFMigrationsHistory")
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, sizes.Select(table => table.Name).Order(StringComparer.Ordinal));

        // A table with no row has its indexes, and they take room.
        Assert.All(sizes, table => Assert.True(table.Bytes > 0, $"{table.Name} is said to take no room."));

        Assert.Equal(
            sizes.OrderByDescending(table => table.Bytes).ThenBy(table => table.Name, StringComparer.Ordinal),
            sizes);
    }

    [Fact]
    public async Task A_table_that_grows_is_said_to_be_larger()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        var before = Assert.Single(await ReadAsync(application), table => table.Name == "background_jobs");

        // Rows of jobs that no application has: the scheduler keeps them and runs nothing for them.
        database.Execute(
            "INSERT INTO background_jobs (name, last_started_at) SELECT 'planted-' || number, now() FROM generate_series(1, 20000) AS number");

        var after = Assert.Single(await ReadAsync(application), table => table.Name == "background_jobs");

        Assert.True(after.Bytes > before.Bytes, $"background_jobs took {before.Bytes} bytes, and takes {after.Bytes} with 20,000 rows more.");
    }

    private static Task<IReadOnlyList<TableSize>> ReadAsync(LoggedApplication application) =>
        application.Services.GetRequiredService<IDatabaseSizes>().ReadAsync(TestContext.Current.CancellationToken);
}
