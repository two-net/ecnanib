using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Persistence;

internal static class PostgresOptions
{
    /// <summary>
    /// Shared by the application and `dotnet ef`. Naming changes the generated schema, so the two
    /// must agree.
    /// </summary>
    public static void UsePostgres(this DbContextOptionsBuilder options, string? connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
