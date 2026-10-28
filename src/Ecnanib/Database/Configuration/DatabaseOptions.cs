using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Database.Configuration;

/// <summary>
/// The <c>database</c> section of the system configuration file (Appendix D): the PostgreSQL connection and the key
/// that encrypts the users' stored API keys, the only credentials the file may contain (FR-CFG-03), plus command
/// timeout, transient-failure retries and how long a start waits for a previous instance to release the database
/// (NFR-REL-04).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Npgsql connection string, e.g. <c>Host=localhost;Database=ecnanib;Username=ecnanib;Password=...</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The base64 encoding of the 32 random bytes under which the users' exchange and provider keys are encrypted at
    /// rest (AES-256-GCM, FR-KEY-04); <c>openssl rand -base64 32</c> makes one. Required, never logged or echoed. Losing
    /// it loses every stored key: users enter them again.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>
    /// For a rotation: the key the database is encrypted under, set beside the new <see cref="EncryptionKey"/> for one
    /// start, which re-encrypts every stored key under the new one; remove it afterwards.
    /// </summary>
    public string? PreviousEncryptionKey { get; set; }

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
