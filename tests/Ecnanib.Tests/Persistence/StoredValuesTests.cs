using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Prices, quantities, and money are stored as exact decimals, and timestamps are stored in UTC
/// (NFR-DAT-8). No entity of the application has such a column yet, so these tests store the
/// values the way an entity will: through the same database provider, with no column type named.
/// </summary>
public class StoredValuesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<TestDatabase> Database = new(() =>
    {
        var database = PostgresServer.CreateDatabase();

        using var ledger = new Ledger(database);
        ledger.Database.EnsureCreated();

        return database;
    });

    [Theory]
    [InlineData("1234567890.123456789012345678")]
    [InlineData("0.000000000000000001")]
    [InlineData("0.3")]
    public void A_decimal_comes_back_exactly_as_it_went_in(string amount)
    {
        var id = Store(new Entry { Amount = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), At = Noon });

        using var ledger = new Ledger(Database.Value);
        Assert.Equal(amount, ledger.Entries.Single(entry => entry.Id == id).Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // And it is that number in the database, not only in what the provider reads back.
        Assert.Equal(amount, Database.Value.Scalar<string>($"""SELECT "Amount"::text FROM "Entries" WHERE "Id" = {id}"""));
        Assert.Equal("numeric", Database.Value.Scalar<string>($"""SELECT pg_typeof("Amount")::text FROM "Entries" WHERE "Id" = {id}"""));
    }

    [Fact]
    public void A_timestamp_is_stored_as_a_moment_in_UTC()
    {
        var id = Store(new Entry { At = Noon, AtAsDateTime = Noon.UtcDateTime });

        using var ledger = new Ledger(Database.Value);
        var stored = ledger.Entries.Single(entry => entry.Id == id);

        Assert.Equal(Noon, stored.At);
        Assert.Equal(TimeSpan.Zero, stored.At.Offset);
        Assert.Equal(DateTimeKind.Utc, stored.AtAsDateTime!.Value.Kind);
        Assert.Equal(
            "timestamp with time zone 2026-11-11 12:00:00",
            Database.Value.Scalar<string>(
                $"""SELECT pg_typeof("At")::text || ' ' || to_char("At" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') FROM "Entries" WHERE "Id" = {id}"""));
    }

    [Fact]
    public void A_time_that_is_not_in_UTC_is_refused()
    {
        var withAnOffset = Assert.ThrowsAny<Exception>(() => Store(new Entry { At = Noon.ToOffset(TimeSpan.FromHours(7)) }));
        Assert.Contains("only offset 0 (UTC) is supported", withAnOffset.ToString());

        var local = Assert.ThrowsAny<Exception>(() =>
            Store(new Entry { At = Noon, AtAsDateTime = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Local) }));
        Assert.Contains("only UTC is supported", local.ToString());

        var unspecified = Assert.ThrowsAny<Exception>(() =>
            Store(new Entry { At = Noon, AtAsDateTime = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Unspecified) }));
        Assert.Contains("only UTC is supported", unspecified.ToString());
    }

    private static int Store(Entry entry)
    {
        using var ledger = new Ledger(Database.Value);
        ledger.Entries.Add(entry);
        ledger.SaveChanges();

        return entry.Id;
    }

    /// <summary>A table with an amount and a time, mapped with nothing but the provider's defaults.</summary>
    private sealed class Ledger(TestDatabase database)
        : DbContext(new DbContextOptionsBuilder().UseNpgsql(database.ConnectionString).Options)
    {
        public DbSet<Entry> Entries => Set<Entry>();
    }

    private sealed class Entry
    {
        public int Id { get; set; }

        public decimal Amount { get; set; }

        public DateTimeOffset At { get; set; }

        public DateTime? AtAsDateTime { get; set; }
    }
}
