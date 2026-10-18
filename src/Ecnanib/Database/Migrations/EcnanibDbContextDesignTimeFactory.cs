using Ecnanib.Database.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecnanib.Database.Migrations;

/// <summary>
/// Builds the context for <c>dotnet ef migrations</c> and for offline tests. Scaffolding a migration never opens a
/// connection, so the placeholder connection string is never used to connect.
/// </summary>
internal sealed class EcnanibDbContextDesignTimeFactory : IDesignTimeDbContextFactory<EcnanibDbContext>
{
    public const string PlaceholderConnectionString = "Host=localhost;Database=ecnanib;Username=ecnanib";

    public EcnanibDbContext CreateDbContext(string[] args) => Create();

    public static EcnanibDbContext Create(string? connectionString = null)
    {
        var builder = new DbContextOptionsBuilder<EcnanibDbContext>();
        NpgsqlConfiguration.Apply(builder, connectionString ?? PlaceholderConnectionString, new DatabaseOptions());
        return new EcnanibDbContext(builder.Options);
    }
}
