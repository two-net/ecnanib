using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecnanib.Tests.Persistence;

/// <summary>Enforces the data rules on the application's model. None of this needs a database.</summary>
public sealed class SchemaRulesTests
{
    [Fact]
    public void The_model_follows_the_data_rules()
    {
        using var context = CreateContext();

        Assert.Empty(SchemaRules.FindViolations(context));
    }

    /// <summary>DATA-R-05: the code-first model owns the schema, so it can't drift from the migrations.</summary>
    [Fact]
    public void Every_model_change_has_a_migration()
    {
        using var context = CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// NFR-MNT-05. Migration.Down throws unless a migration overrides it, so deleting the generated
    /// Down makes rollback impossible. If this fails, delete it from the migration named here.
    /// </summary>
    [Fact]
    public void Migrations_are_forward_only()
    {
        using var context = CreateContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        Assert.NotEmpty(migrations.Migrations);
        Assert.All(migrations.Migrations.Values, type =>
        {
            var migration = migrations.CreateMigration(type, context.Database.ProviderName!);

            Assert.Throws<NotSupportedException>(() => migration.DownOperations);
        });
    }

    private static EcnanibDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<EcnanibDbContext>().UseNpgsql().UseSnakeCaseNamingConvention().Options);
}
