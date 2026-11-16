using Ecnanib.Accounts;

namespace Ecnanib.Agents;

/// <summary>Agents: agent configuration and lifecycle (SRS 3.1). See docs/agents.md.</summary>
public static class AgentsModule
{
    /// <summary>
    /// Registers the component. Whether an agent holds a position or orders is asked through
    /// <see cref="IAgentExposure"/>, which whatever keeps them implements and registers.
    /// </summary>
    public static IServiceCollection AddAgents(this IServiceCollection services)
    {
        services.AddSingleton<IStoredAgents, StoredAgents>();

        // What Accounts asks before it deletes an account: whether an agent of the user is
        // active (FR-ACC-9).
        services.AddSingleton<IAgentActivity, AgentActivity>();

        // The part of the export of a user's data that this component keeps (NFR-SEC-9).
        services.AddSingleton<IExportedPart, AgentsExport>();

        return services;
    }
}
