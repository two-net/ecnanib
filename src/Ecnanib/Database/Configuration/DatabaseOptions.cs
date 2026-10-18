using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Database.Configuration;

/// <summary>
/// The <c>database</c> section of the system configuration file (Appendix D): the PostgreSQL connection, the only
/// credential the file may contain (FR-CFG-03), plus command timeout and transient-failure retries.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Npgsql connection string, e.g. <c>Host=localhost;Database=ecnanib;Username=ecnanib;Password=...</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Retries of a command that failed with a transient PostgreSQL error; 0 disables the retrying execution strategy.</summary>
    [Range(0, 10)]
    public int RetryOnFailureCount { get; set; } = 3;

    public TimeSpan CommandTimeout => TimeSpan.FromSeconds(CommandTimeoutSeconds);
}
