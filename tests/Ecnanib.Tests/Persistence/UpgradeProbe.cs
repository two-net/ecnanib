using System.Collections.Immutable;
using System.Reflection;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Stands in for the next release's migration, which proves the upgrade path while the real chain
/// has too few migrations to upgrade between.
/// </summary>
[DbContext(typeof(EcnanibDbContext))]
[Migration(Id)]
internal sealed class UpgradeProbe : Migration
{
    public const string Id = "99990101000000_UpgradeProbe";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TABLE upgrade_probe (id integer)");
}

/// <summary>
/// The application's real migrations plus <see cref="UpgradeProbe"/>. This implements the interface
/// because EF's own MigrationsAssembly is an internal API, which analyzer EF1001 rejects.
/// </summary>
internal sealed class MigrationsWithUpgradeProbe : IMigrationsAssembly
{
    public static void AddTo(IServiceCollection services) =>
        services.ConfigureDbContext<EcnanibDbContext>(
            options => options.ReplaceService<IMigrationsAssembly, MigrationsWithUpgradeProbe>(),
            ServiceLifetime.Singleton);

    public Assembly Assembly { get; } = typeof(EcnanibDbContext).Assembly;

    public IReadOnlyDictionary<string, TypeInfo> Migrations { get; } = typeof(EcnanibDbContext).Assembly.DefinedTypes
        .Append(typeof(UpgradeProbe).GetTypeInfo())
        .Where(type => type.IsSubclassOf(typeof(Migration)))
        .ToImmutableSortedDictionary(type => type.GetCustomAttribute<MigrationAttribute>()!.Id, type => type);

    public ModelSnapshot? ModelSnapshot { get; } = typeof(EcnanibDbContext).Assembly.DefinedTypes
        .Where(type => type.IsSubclassOf(typeof(ModelSnapshot)))
        .Select(type => (ModelSnapshot)Activator.CreateInstance(type)!)
        .Single();

    public string? FindMigrationId(string nameOrId) =>
        Migrations.Keys.FirstOrDefault(id => id == nameOrId || id.EndsWith($"_{nameOrId}", StringComparison.Ordinal));

    public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        var migration = (Migration)Activator.CreateInstance(migrationClass)!;
        migration.ActiveProvider = activeProvider;
        return migration;
    }
}
