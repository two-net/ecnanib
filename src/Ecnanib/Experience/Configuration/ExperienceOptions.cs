using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Experience.Configuration;

/// <summary>
/// The <c>Experience</c> section of the system configuration file: how often the statistics are recomputed
/// (FR-EXP-04), how long a rejected setup is followed (FR-EXP-03), how many recent outcomes a role prompt carries
/// (FR-EXP-06) and the sample a suggestion needs (FR-EXP-08). Appendix D lists no such section; FR-CFG-08 makes the
/// appendix a minimum. Contains no credentials (FR-CFG-03).
/// </summary>
public sealed class ExperienceOptions
{
    public const string SectionName = "Experience";

    /// <summary>Minutes between two recomputations of the experience statistics.</summary>
    [Range(1, 1440)]
    public int RefreshIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Candles a rejected setup is followed for after its entry would have filled before it expires unresolved. With
    /// the entry timeout (at most 100 candles) it must fit the 300 candles an evaluation sees.
    /// </summary>
    [Range(1, 200)]
    public int ShadowHorizonCandles { get; set; } = 30;

    /// <summary>Most recent outcomes (trades and resolved rejections) listed in a role's experience summary.</summary>
    [Range(1, 20)]
    public int RecentOutcomes { get; set; } = 5;

    /// <summary>Closed trades a provider and model need in the user's history before it is suggested for a role.</summary>
    [Range(1, 1000)]
    public int MinimumTradesForSuggestion { get; set; } = 10;

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(RefreshIntervalMinutes);
}
