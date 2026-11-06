using System.Globalization;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// The Agents section (SRS 7.3), and the one key of the Security section that's about agents. Read() is the only way
/// in. A missing value takes its default, and an invalid one stops startup with a message that names it (CFG-03). The
/// section's other settings belong to the issues that use them: the cycle timings to the scheduler's
/// <c>SchedulerSettings</c>, and the approval windows to the decision cycle's <c>DecisionCycleSettings</c>.
/// </summary>
internal sealed class AgentsSettings
{
    public const string Section = "Agents";

    public const string MaxAgentsPerUserKey = Section + ":MaxAgentsPerUser";

    public const string DefaultEnvironmentKey = Section + ":DefaultEnvironment";

    public const string RequireTwoFactorForLiveKey = "Security:RequireTwoFactorForLive";

    public const int DefaultMaxAgentsPerUser = 10;

    public const int MaximumAgentsPerUser = 1000;

    private AgentsSettings(int maxAgentsPerUser, bool requireTwoFactorForLive)
    {
        MaxAgentsPerUser = maxAgentsPerUser;
        RequireTwoFactorForLive = requireTwoFactorForLive;
    }

    /// <summary>The most agents one user may have that aren't archived (FR-AGT-01).</summary>
    public int MaxAgentsPerUser { get; }

    /// <summary>
    /// The environment a new agent starts in, which is always Demo: an agent becomes Live only when its user switches it
    /// (UC-04), so <c>Agents:DefaultEnvironment</c> may only say Demo.
    /// </summary>
    public TradingEnvironment DefaultEnvironment => TradingEnvironment.Demo;

    /// <summary>Whether switching an agent to Live needs two-factor turned on (FR-AGT-05). Off by default.</summary>
    public bool RequireTwoFactorForLive { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static AgentsSettings Read(IConfiguration configuration)
    {
        CheckDefaultEnvironment(configuration[DefaultEnvironmentKey]);

        return new AgentsSettings(
            ReadMaxAgentsPerUser(configuration[MaxAgentsPerUserKey]),
            ReadRequireTwoFactorForLive(configuration[RequireTwoFactorForLiveKey]));
    }

    private static int ReadMaxAgentsPerUser(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMaxAgentsPerUser;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{MaxAgentsPerUserKey} must be a whole number.");
        }

        if (number is < 1 or > MaximumAgentsPerUser)
        {
            throw new InvalidOperationException($"{MaxAgentsPerUserKey} must be between 1 and {MaximumAgentsPerUser}.");
        }

        return number;
    }

    /// <summary>
    /// SRS 7.3 lists the setting, and FR-AGT-02 lets an agent be Live, but the "Demo first" principle (SRS 2.8) and
    /// UC-04 make Live an explicit opt-in for each agent. A Live default would skip it, so it's refused by name.
    /// </summary>
    private static void CheckDefaultEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), nameof(TradingEnvironment.Demo), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{DefaultEnvironmentKey} must be Demo. Every agent starts in Demo, and its user switches it to Live with \"Switch to Live\".");
    }

    private static bool ReadRequireTwoFactorForLive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return bool.TryParse(value, out var required)
            ? required
            : throw new InvalidOperationException($"{RequireTwoFactorForLiveKey} must be true or false.");
    }
}

/// <summary>
/// Says at startup how many agents each user may have, and whether Live needs two-factor. The host builds every hosted
/// service before it starts any, and this one takes <see cref="AgentsSettings"/>, so an invalid setting stops startup
/// before any migration runs (CFG-03).
/// </summary>
internal sealed class AgentsStartup(AgentsSettings settings, ILogger<AgentsStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Each user may have up to {MaxAgentsPerUser} agents that aren't archived, which start in {Environment}. Switching one to Live needs two-factor: {RequireTwoFactorForLive}",
            settings.MaxAgentsPerUser,
            settings.DefaultEnvironment,
            settings.RequireTwoFactorForLive);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
