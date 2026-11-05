using System.Globalization;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// The Agents section (SRS 7.3). Read() is the only way in. A missing value takes its default, and an invalid
/// one stops startup with a message that names it (CFG-03). The section's other settings belong to the
/// issues that use them: the default environment to switching to Live (#39), and the cycle timings to the
/// scheduler and approvals (#30, #38).
/// </summary>
internal sealed class AgentsSettings
{
    public const string Section = "Agents";

    public const string MaxAgentsPerUserKey = Section + ":MaxAgentsPerUser";

    public const int DefaultMaxAgentsPerUser = 10;

    public const int MaximumAgentsPerUser = 1000;

    private AgentsSettings(int maxAgentsPerUser) => MaxAgentsPerUser = maxAgentsPerUser;

    /// <summary>The most agents one user may have that aren't archived (FR-AGT-01).</summary>
    public int MaxAgentsPerUser { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static AgentsSettings Read(IConfiguration configuration)
    {
        var value = configuration[MaxAgentsPerUserKey];

        if (string.IsNullOrWhiteSpace(value))
        {
            return new AgentsSettings(DefaultMaxAgentsPerUser);
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{MaxAgentsPerUserKey} must be a whole number.");
        }

        if (number is < 1 or > MaximumAgentsPerUser)
        {
            throw new InvalidOperationException($"{MaxAgentsPerUserKey} must be between 1 and {MaximumAgentsPerUser}.");
        }

        return new AgentsSettings(number);
    }
}

/// <summary>
/// Says at startup how many agents each user may have. The host builds every hosted service before it starts
/// any, and this one takes <see cref="AgentsSettings"/>, so an invalid setting stops startup before any
/// migration runs (CFG-03).
/// </summary>
internal sealed class AgentsStartup(AgentsSettings settings, ILogger<AgentsStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Each user may have up to {MaxAgentsPerUser} agents that aren't archived", settings.MaxAgentsPerUser);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
