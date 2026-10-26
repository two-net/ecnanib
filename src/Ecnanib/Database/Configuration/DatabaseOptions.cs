using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Database.Configuration;

/// <summary>
/// The <c>database</c> section of the system configuration file (Appendix D): the PostgreSQL connection, the only
/// credential the file may contain (FR-CFG-03), plus command timeout, transient-failure retries and how long a start
/// waits for a previous instance to release the database (NFR-REL-04).
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

    /// <summary>How long a starting instance waits for the previous one to release the database's instance lock before it refuses to start (NFR-REL-04).</summary>
    [Range(0, 600)]
    public int InstanceLockWaitSeconds { get; set; } = 30;

    public TimeSpan CommandTimeout => TimeSpan.FromSeconds(CommandTimeoutSeconds);

    public TimeSpan InstanceLockWait => TimeSpan.FromSeconds(InstanceLockWaitSeconds);
}
