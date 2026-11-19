using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Reads that must agree with each other are made in a snapshot: every statement of it reads the
/// database as it was when the first of them began. What works out the equity of an agent reads
/// this way, so that an order that is executed between two of its reads is counted once
/// (FR-RSK-8). See "Reads that agree" in docs/persistence.md.
/// </summary>
public class SnapshotTests
{
    [Fact]
    public async Task The_reads_of_a_snapshot_see_the_database_as_it_was_at_the_first_of_them()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        await using var reader = database.Context();

        await using (await reader.BeginSnapshotAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(0, await RowsAsync(reader));

            // Another transaction writes a row, and commits it.
            database.Execute("INSERT INTO background_jobs (name, last_started_at) VALUES ('first', now())");

            // The second read is of the same moment as the first: the two agree.
            Assert.Equal(0, await RowsAsync(reader));
        }

        // An ordinary transaction is not one: each of its statements sees what was committed
        // before the statement began, so two of them can stand on either side of a write.
        await using (await reader.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, await RowsAsync(reader));

            database.Execute("INSERT INTO background_jobs (name, last_started_at) VALUES ('second', now())");

            Assert.Equal(2, await RowsAsync(reader));
        }
    }

    private static Task<int> RowsAsync(EcnanibDbContext context) =>
        context.Database
            .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM background_jobs""")
            .SingleAsync(TestContext.Current.CancellationToken);
}
