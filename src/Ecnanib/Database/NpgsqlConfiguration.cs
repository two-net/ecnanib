using Ecnanib.Database.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Database;

/// <summary>
/// The single place that configures the PostgreSQL provider, shared by <c>AddDatabase</c> and the design-time
/// factory so the model snapshot and the runtime model never drift.
/// </summary>
internal static class NpgsqlConfiguration
{
    /// <summary>Lowest supported server version; <c>NULLS NOT DISTINCT</c> unique indexes need 15 (DB-01).</summary>
    public static readonly Version MinimumPostgresVersion = new(15, 0);

    public static DbContextOptionsBuilder Apply(DbContextOptionsBuilder builder, string connectionString, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.CommandTimeout(options.CommandTimeoutSeconds);
                npgsql.SetPostgresVersion(MinimumPostgresVersion);
                if (options.RetryOnFailureCount > 0)
                    npgsql.EnableRetryOnFailure(options.RetryOnFailureCount);
            })
            .UseSnakeCaseNamingConvention();
    }
}
