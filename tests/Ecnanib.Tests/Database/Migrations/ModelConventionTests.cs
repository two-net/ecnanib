using System.Text.Json;
using Ecnanib.Database.Entities;
using Ecnanib.Database.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Database.Migrations;

/// <summary>The model-wide conventions every entity relies on.</summary>
public class ModelConventionTests
{
    private static IEnumerable<IProperty> AllProperties(IModel model) =>
        model.GetEntityTypes().SelectMany(e => e.GetProperties());

    [Fact]
    public void TimestampsAreUtcTimestamptzEnumsAreTextDecimalsAreUnboundedAndJsonIsJsonb()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();
        var inspected = 0;

        foreach (var property in AllProperties(db.Model))
        {
            var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (clrType == typeof(DateTimeOffset))
            {
                Assert.Equal("timestamp with time zone", property.GetColumnType());
                Assert.NotNull(property.GetValueConverter());
                inspected++;
            }
            else if (clrType.IsEnum)
            {
                var ordinal = clrType == typeof(LogLevel) || clrType == typeof(DayOfWeek);
                Assert.Equal(ordinal ? "integer" : "text", property.GetColumnType());
                inspected++;
            }
            else if (clrType == typeof(decimal))
            {
                Assert.Equal("numeric", property.GetColumnType());
                Assert.Null(property.GetPrecision());
                inspected++;
            }
            else if (clrType == typeof(JsonElement))
            {
                Assert.Equal("jsonb", property.GetColumnType());
                inspected++;
            }
        }

        Assert.True(inspected > 60, $"Only {inspected} properties inspected.");
    }

    [Fact]
    public void EveryForeignKeyHasAnExplicitServerSideDeleteBehaviour()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        foreach (var fk in db.Model.GetEntityTypes().Where(e => !e.IsOwned()).SelectMany(e => e.GetForeignKeys()))
        {
            Assert.True(fk.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull or DeleteBehavior.NoAction,
                $"{fk.DeclaringEntityType.ShortName()} -> {fk.PrincipalEntityType.ShortName()} uses {fk.DeleteBehavior}.");
            if (fk.PrincipalEntityType.ClrType == typeof(User))
                Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
        }
    }

    [Fact]
    public void OrdinalEnumsAreStoredAsIntegersSoRangesCompareCorrectly()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        var sql = db.LogEntries.Where(l => l.Level >= LogLevel.Warning).ToQueryString();

        Assert.Contains("level >= 3", sql);
        Assert.Equal("integer", db.Model.FindEntityType(typeof(EntryWindow))!.FindProperty(nameof(EntryWindow.StartDay))!.GetColumnType());
        Assert.Equal("text", db.Model.FindEntityType(typeof(Agent))!.FindProperty(nameof(Agent.State))!.GetColumnType());
    }

    /// <summary>FR-KEY-04: the re-validation guards its update with the stored ciphertext, so byte[] equality must reach the database as a bytea comparison.</summary>
    [Fact]
    public void StoredKeyBytesCompareInTheDatabase()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();
        var checkedKey = new byte[] { 1, 2, 3 };

        var sql = db.ExchangeKeys.Where(k => k.ProtectedApiKey == checkedKey).ToQueryString();

        Assert.Contains("e.api_key = @checkedKey", sql);
        Assert.Equal("bytea", db.Model.FindEntityType(typeof(ExchangeKey))!.FindProperty(nameof(ExchangeKey.ProtectedApiKey))!.GetColumnType());
        Assert.Equal("bytea", db.Model.FindEntityType(typeof(ProviderKey))!.FindProperty(nameof(ProviderKey.ProtectedApiKey))!.GetColumnType());
        Assert.Equal("text", db.Model.FindEntityType(typeof(ProviderKey))!.FindProperty(nameof(ProviderKey.Cipher))!.GetColumnType());
    }

    [Fact]
    public void LogEntriesHaveNoForeignKeys()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        Assert.Empty(db.Model.FindEntityType(typeof(LogEntry))!.GetForeignKeys());
    }

    [Theory]
    [InlineData(typeof(Agent))]
    [InlineData(typeof(Order))]
    [InlineData(typeof(Trade))]
    public void MutableAggregatesUseXminForConcurrency(Type entityType)
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        var version = db.Model.FindEntityType(entityType)!.FindProperty("Version")!;

        Assert.True(version.IsConcurrencyToken);
        Assert.Equal("xmin", version.GetColumnName());
    }

    [Fact]
    public void UniqueKeysGuardIdempotencyAndDuplicates()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        Assert.Contains(db.Model.FindEntityType(typeof(Evaluation))!.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["AgentId", "CandleOpenTime"]));
        Assert.Contains(db.Model.FindEntityType(typeof(Order))!.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["ClientOrderId"]));
        Assert.Contains(db.Model.FindEntityType(typeof(ProviderKey))!.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["UserId", "Provider"]));
        Assert.Contains(db.Model.FindEntityType(typeof(ExperienceStat))!.GetIndexes(), i => i.IsUnique && i.Properties.Count == 8);
    }
}
