using Ecnanib.Accounts;
using Ecnanib.Catalog;
using Ecnanib.Credentials;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.RiskGuard;

namespace Ecnanib.Agents;

/// <summary>Agents: agent configuration and lifecycle (SRS 3.1). See docs/agents.md.</summary>
public static class AgentsModule
{
    /// <summary>
    /// Registers the component. What an agent holds on the exchange is asked through
    /// <see cref="IAgentExposure"/>, and its position is closed through it, which the Decision
    /// Engine implements and registers: it keeps the positions and the orders.
    /// </summary>
    public static IServiceCollection AddAgents(this IServiceCollection services)
    {
        services.AddSingleton<IStoredAgents, StoredAgents>();

        // What gives the roles of an agent's team their models, and asks Credentials which
        // models a user can select (FR-AGT-5, FR-CAT-6, FR-CAT-9).
        services.AddSingleton<AgentRoles>();

        // What starts, pauses, resumes, and stops an agent, and closes its position by hand
        // (FR-AGT-1, FR-AGT-10 to FR-AGT-12).
        services.AddSingleton<IAgentControls, AgentControls>();

        // What switches an agent between Demo and Live (FR-AGT-7).
        services.AddSingleton<IAgentModes, AgentModes>();

        // What Credentials tells when a key is deleted or refused: an agent that runs with it
        // moves to Attention required (FR-KEY-10).
        services.AddSingleton<IKeyDependents, AgentKeyDependents>();

        // What the Catalog tells when a sync marks a model unavailable: an agent that runs with
        // it moves to Attention required, and no other model is put in its place (FR-CAT-8).
        services.AddSingleton<IModelDependents, AgentModelDependents>();

        // What starts a cycle asks which agents run, of every user, when a candle closes, and
        // whether an agent still runs when the turn of its cycle comes (FR-CYC-1).
        services.AddSingleton<IRunningAgents, RunningAgents>();

        // What runs a test cycle asks for the agent of its user, in whatever state, with its
        // team and with what keeps the team from working (FR-CYC-12).
        services.AddSingleton<IAgentTeams, AgentTeams>();

        // What whatever places the orders of an agent asks: whether the agent may still open a
        // position, as the entry is written, and that it wait for its user when its position is
        // left without protection (FR-RSK-7).
        services.AddSingleton<IAgentTrading, AgentTrading>();

        // What whatever watches the loss limits of an agent asks: that an agent which reached
        // one be halted, and that an agent which its daily loss limit halted run again when the
        // next UTC day begins (FR-RSK-8).
        services.AddSingleton<IAgentHalts, AgentHalts>();

        // What keeps the global halt (FR-OPS-3): an operator reads and switches it, and what
        // checks the plan of a cycle asks the Risk Guard's question of it, whether it is on.
        // One instance answers both.
        services.AddSingleton<GlobalHaltSwitch>();
        services.AddSingleton<IGlobalHaltSwitch>(provider => provider.GetRequiredService<GlobalHaltSwitch>());
        services.AddSingleton<IGlobalHalt>(provider => provider.GetRequiredService<GlobalHaltSwitch>());

        // What gives the roles of an agent that opted in other models by strength, on its own,
        // and the job that has it do so every hour (FR-TEAM-10).
        services.AddSingleton<RoleReassignments>();
        services.AddBackgroundJob<RoleReassignmentJob>();

        // What Accounts asks before it deletes an account: whether an agent of the user is
        // active (FR-ACC-9).
        services.AddSingleton<IAgentActivity, AgentActivity>();

        // The parts of the export of a user's data that this component keeps (NFR-SEC-9): the
        // agents, the models of their roles, and the changes of their states.
        services.AddSingleton<IExportedPart, AgentsExport>();
        services.AddSingleton<IExportedPart, AgentRolesExport>();
        services.AddSingleton<IExportedPart, AgentStateChangesExport>();

        // And the part that Experience keeps: the lessons of the agents (FR-EXP-8). Experience
        // may not use Accounts, which declares what an export is made of, so that what recalls
        // lessons into prompts cannot reach personal data (FR-PRM-9). This component may use both.
        services.AddSingleton<IExportedPart, LessonsExport>();

        // And who left the platform-wide board, which Experience keeps as well (FR-EXP-10): a
        // preference of the user, handed over for the same reason.
        services.AddSingleton<IExportedPart, BoardOptOutsExport>();

        return services;
    }
}
