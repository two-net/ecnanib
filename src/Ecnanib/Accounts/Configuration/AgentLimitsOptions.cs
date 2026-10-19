using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Accounts.Configuration;

/// <summary>
/// The fair-use limits of the <c>agents</c> section of the system configuration file (Appendix D, FR-ACC-07): the same
/// for every user, enforced at agent creation and start (FR-AGT-15). The agents module binds its own settings
/// (deadlines, concurrency, entry timeout) to the same section.
/// </summary>
public sealed class AgentLimitsOptions
{
    public const string SectionName = "Agents";

    [Range(1, 1000)]
    public int MaxAgentsPerUser { get; set; } = 10;

    /// <summary>Running and Paused agents count; a paused agent resumes without a start check.</summary>
    [Range(1, 1000)]
    public int MaxRunningAgentsPerUser { get; set; } = 3;
}
