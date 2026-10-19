using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Keys.Configuration;

/// <summary>
/// The <c>Keys</c> section of the system configuration file: how often stored keys are re-validated (FR-KEY-07) and
/// how long one validation may take. Appendix D lists no such section; FR-CFG-08 makes the appendix a minimum.
/// </summary>
public sealed class KeysOptions
{
    public const string SectionName = "Keys";

    /// <summary>A key whose last check is older than this is re-validated by the background job.</summary>
    [Range(1, 10080)]
    public int RevalidationIntervalMinutes { get; set; } = 60;

    /// <summary>Upper bound of one validation, including the exchange and AI modules' own retries, after which it counts as not checked.</summary>
    [Range(5, 600)]
    public int ValidationTimeoutSeconds { get; set; } = 60;

    public TimeSpan RevalidationInterval => TimeSpan.FromMinutes(RevalidationIntervalMinutes);

    public TimeSpan ValidationTimeout => TimeSpan.FromSeconds(ValidationTimeoutSeconds);
}
