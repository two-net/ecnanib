using Microsoft.Extensions.Options;
using Npgsql;

namespace Ecnanib.Database.Configuration;

/// <summary>
/// Validates the <c>Database</c> section at startup so an invalid file refuses to start (FR-CFG-06). Failure
/// messages never echo the connection string, which carries the database password.
/// </summary>
public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        var failures = new List<string>();
        var prefix = DatabaseOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add($"{prefix}:ConnectionString is required.");
        }
        else
        {
            try
            {
                var csb = new NpgsqlConnectionStringBuilder(options.ConnectionString);
                if (string.IsNullOrWhiteSpace(csb.Host))
                    failures.Add($"{prefix}:ConnectionString must name a Host.");
                if (string.IsNullOrWhiteSpace(csb.Database))
                    failures.Add($"{prefix}:ConnectionString must name a Database.");
            }
            catch (ArgumentException ex)
            {
                failures.Add($"{prefix}:ConnectionString is not a valid PostgreSQL connection string: {ex.Message}");
            }
        }

        if (options.CommandTimeoutSeconds <= 0)
            failures.Add($"{prefix}:CommandTimeoutSeconds must be > 0.");
        if (options.RetryOnFailureCount < 0)
            failures.Add($"{prefix}:RetryOnFailureCount must be >= 0.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
