using Ecnanib.Accounts;
using Ecnanib.Credentials;

namespace Ecnanib.Agents;

/// <summary>Agents: agent configuration and lifecycle (SRS 3.1). See docs/agents.md.</summary>
public static class AgentsModule
{
    /// <summary>
    /// Registers the component. What an agent holds on the exchange is asked through
    /// <see cref="IAgentExposure"/>, and its position is closed through it, which whatever keeps
    /// positions and orders implements and registers.
    /// </summary>
    public static IServiceCollection AddAgents(this IServiceCollection services)
    {
        services.AddSingleton<IStoredAgents, StoredAgents>();

        // What starts, pauses, resumes, and stops an agent, and closes its position by hand
        // (FR-AGT-1, FR-AGT-10 to FR-AGT-12).
        services.AddSingleton<IAgentControls, AgentControls>();

        // What Credentials tells when a key is deleted or refused: an agent that runs with it
        // moves to Attention required (FR-KEY-10).
        services.AddSingleton<IKeyDependents, AgentKeyDependents>();

        // What Accounts asks before it deletes an account: whether an agent of the user is
        // active (FR-ACC-9).
        services.AddSingleton<IAgentActivity, AgentActivity>();

        // The parts of the export of a user's data that this component keeps (NFR-SEC-9): the
        // agents, and the changes of their states.
        services.AddSingleton<IExportedPart, AgentsExport>();
        services.AddSingleton<IExportedPart, AgentStateChangesExport>();

        return services;
    }
}
