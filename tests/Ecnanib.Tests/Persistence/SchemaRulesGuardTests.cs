using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Tests the schema rules against a deliberately broken model. The application's model has almost
/// no columns yet, so a rule that silently stopped matching would be invisible without this.
/// </summary>
public sealed class SchemaRulesGuardTests
{
    private static readonly List<string> Violations = FindViolations();

    [Theory]
    [InlineData(nameof(BrokenEntity.Double), "DATA-R-02")]
    [InlineData(nameof(BrokenEntity.NullableFloat), "DATA-R-02")]
    [InlineData(nameof(BrokenEntity.DecimalStoredAsDouble), "DATA-R-02")]
    [InlineData(nameof(BrokenEntity.NaiveTimestamp), "DATA-R-01")]
    [InlineData(nameof(BrokenEntity.NullableNaiveTimestamp), "DATA-R-01")]
    [InlineData(nameof(BrokenEntity.TimestampStoredAsText), "DATA-R-01")]
    public void A_broken_column_is_reported(string property, string rule) =>
        Assert.Contains(Violations, violation =>
            violation.Contains($".{property}:", StringComparison.Ordinal)
            && violation.Contains(rule, StringComparison.Ordinal));

    [Theory]
    [InlineData(nameof(BrokenEntity.Exact))]
    [InlineData(nameof(BrokenEntity.UtcDateTime))]
    [InlineData(nameof(BrokenEntity.UtcDateTimeOffset))]
    public void A_compliant_column_is_not_reported(string property) =>
        Assert.DoesNotContain(Violations, violation => violation.Contains($".{property}:", StringComparison.Ordinal));

    private static List<string> FindViolations()
    {
        using var context = new BrokenContext();

        return [.. SchemaRules.FindViolations(context)];
    }

    private sealed class BrokenContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<BrokenEntity>();
            entity.Property(broken => broken.DecimalStoredAsDouble).HasConversion<double>();
            entity.Property(broken => broken.TimestampStoredAsText).HasConversion<string>();
        }
    }

    private sealed class BrokenEntity
    {
        public int Id { get; set; }

        public double Double { get; set; }

        public float? NullableFloat { get; set; }

        public decimal DecimalStoredAsDouble { get; set; }

        [Column(TypeName = "timestamp without time zone")]
        public DateTime NaiveTimestamp { get; set; }

        [Column(TypeName = "timestamp without time zone")]
        public DateTime? NullableNaiveTimestamp { get; set; }

        public DateTimeOffset TimestampStoredAsText { get; set; }

        public decimal Exact { get; set; }

        public DateTime UtcDateTime { get; set; }

        public DateTimeOffset UtcDateTimeOffset { get; set; }
    }
}
