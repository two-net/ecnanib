using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// The schema is defined by the code: migrations are generated from the model and versioned with
/// it (NFR-DAT-2). No table is partitioned (NFR-DAT-4). Prices, quantities, and money are exact
/// decimals, and timestamps are in UTC (NFR-DAT-8). These tests read the model and need no database.
/// </summary>
public class ModelTests
{
    [Fact]
    public void The_migrations_match_the_model()
    {
        using var context = ModelOnly.Context();

        Assert.NotEmpty(context.Database.GetMigrations());
        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The model has changed since the last migration. Generate one, as docs/persistence.md describes: " +
            "dotnet ef migrations add <Name> --project src/Ecnanib --output-dir Platform/Persistence/Migrations");
    }

    [Fact]
    public void No_migration_partitions_a_table()
    {
        using var context = ModelOnly.Context();

        // Everything the migrations run, from an empty database to the current model.
        var script = context.GetService<IMigrator>().GenerateScript();

        Assert.Contains("CREATE TABLE", script);
        Assert.DoesNotContain("PARTITION", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INHERITS", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_model_has_no_inexact_number_and_no_time_that_is_not_UTC()
    {
        using var context = ModelOnly.Context();

        var violations = SchemaRules.FindViolations(context.Model);

        Assert.True(
            violations.Count == 0,
            "Prices, quantities, and money are stored as exact decimals, and timestamps in UTC:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void An_inexact_number_or_a_time_that_is_not_UTC_is_found()
    {
        using var context = new PlantedContext();

        Assert.Equal(
            [
                "Planted.LocalTime (timestamp without time zone): the column type does not store a moment in UTC. Use timestamp with time zone, the default.",
                "Planted.LocalTimeToTheMillisecond (timestamp(3) without time zone): the column type does not store a moment in UTC. Use timestamp with time zone, the default.",
                "Planted.Money (money): the column type is not an exact decimal. Use numeric, the default for decimal.",
                "Planted.Price (double precision): a floating-point number is not exact. Use decimal.",
                "Planted.Quantity (real): a floating-point number is not exact. Use decimal.",
            ],
            SchemaRules.FindViolations(context.Model));
    }

    [Fact]
    public void A_decimal_and_a_timestamp_are_exact_and_in_UTC_without_being_told()
    {
        // The model relies on these defaults of the database provider, so they are pinned here.
        using var context = new PlantedContext();
        var planted = context.Model.FindEntityType(typeof(Planted))!;

        Assert.Equal("numeric", planted.FindProperty(nameof(Planted.Exact))!.GetColumnType());
        Assert.Equal("timestamp with time zone", planted.FindProperty(nameof(Planted.At))!.GetColumnType());
        Assert.Equal("timestamp with time zone", planted.FindProperty(nameof(Planted.AtAsDateTime))!.GetColumnType());

        // A precision changes the name of the type and not what it stores.
        Assert.Equal("timestamp(3) with time zone", planted.FindProperty(nameof(Planted.AtToTheMillisecond))!.GetColumnType());
    }

    /// <summary>A model with deliberate violations, and with the columns the rules ask for.</summary>
    private sealed class PlantedContext() : DbContext(new DbContextOptionsBuilder().UseNpgsql().Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Planted>(planted =>
            {
                planted.Property(row => row.Money).HasColumnType("money");
                planted.Property(row => row.LocalTime).HasColumnType("timestamp without time zone");
                planted.Property(row => row.LocalTimeToTheMillisecond).HasColumnType("timestamp(3) without time zone");
                planted.Property(row => row.AtToTheMillisecond).HasPrecision(3);
            });
    }

    private sealed class Planted
    {
        public int Id { get; set; }

        public double Price { get; set; }

        public float? Quantity { get; set; }

        public decimal Money { get; set; }

        public DateTime LocalTime { get; set; }

        public decimal Exact { get; set; }

        public DateTimeOffset At { get; set; }

        public DateTime AtAsDateTime { get; set; }

        public DateTime LocalTimeToTheMillisecond { get; set; }

        public DateTimeOffset AtToTheMillisecond { get; set; }
    }
}
