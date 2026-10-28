using Microsoft.Extensions.Options;
using Npgsql;

namespace Ecnanib.Database.Configuration;

/// <summary>
/// Validates the <c>Database</c> section at startup so an invalid file refuses to start (FR-CFG-06). Failure
/// messages never echo the connection string, which carries the database password, nor either encryption key.
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

        if (string.IsNullOrWhiteSpace(options.EncryptionKey))
            failures.Add($"{prefix}:EncryptionKey is required: the base64 encoding of 32 random bytes (openssl rand -base64 32).");
        else if (!StoredKeyProtector.TryParseKey(options.EncryptionKey, out _))
            failures.Add($"{prefix}:EncryptionKey must be the base64 encoding of exactly {StoredKeyProtector.KeySize} bytes.");

        if (!string.IsNullOrWhiteSpace(options.PreviousEncryptionKey))
        {
            if (!StoredKeyProtector.TryParseKey(options.PreviousEncryptionKey, out _))
                failures.Add($"{prefix}:PreviousEncryptionKey must be the base64 encoding of exactly {StoredKeyProtector.KeySize} bytes.");
            else if (string.Equals(options.PreviousEncryptionKey.Trim(), options.EncryptionKey?.Trim(), StringComparison.Ordinal))
                failures.Add($"{prefix}:PreviousEncryptionKey must differ from {prefix}:EncryptionKey; remove it once the rotation has run.");
        }

        if (options.CommandTimeoutSeconds <= 0)
            failures.Add($"{prefix}:CommandTimeoutSeconds must be > 0.");
        if (options.RetryOnFailureCount < 0)
            failures.Add($"{prefix}:RetryOnFailureCount must be >= 0.");
        if (options.InstanceLockWaitSeconds is < 0 or > 600)
            failures.Add($"{prefix}:InstanceLockWaitSeconds must be between 0 and 600.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
