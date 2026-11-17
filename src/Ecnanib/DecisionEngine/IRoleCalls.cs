using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Catalog;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What the model of a role is called with: the model that the agent's user gave the role, the
/// prompt that was composed for the call, the schema of what the role answers, and whether the
/// model may search live for it (FR-CYC-3). See "Live search" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the Sentiment Analyst searches live,</b> and only where the configuration file says
/// that its model can: the capability flags of the file decide, and nothing else. A Sentiment
/// Analyst whose model cannot search is called all the same, without a search, and its report is
/// marked as resting on no live data: see <see cref="SentimentReport"/>. No other role searches,
/// whatever its model can do: what the web says reaches the team through one role alone, whose
/// report the Strategist reads as untrusted content (FR-PRM-8). The Risk Manager is not given
/// the report: what a search found reaches it only in the words of the Strategist's plan.
/// </para>
/// <para>
/// It makes no call. What runs the roles of a cycle hands the request to the gateway with the
/// key of the agent's user, and decides whether a call is made again (FR-TEAM-6, FR-TEAM-9).
/// </para>
/// </remarks>
public interface IRoleCalls
{
    /// <summary>The call of one role of an agent's team, for a prompt that was composed for it.</summary>
    /// <param name="role">The role, with the model that the agent's user gave it, as a cycle is handed it.</param>
    /// <param name="prompt">The prompt of the call, as <see cref="IPromptComposition"/> composed it for the role.</param>
    /// <param name="resultSchema">The JSON Schema of what the role answers.</param>
    RoleCall Of(AgentRoleView role, ComposedPrompt prompt, string resultSchema);
}

/// <summary>One call of a role's model, before it is made.</summary>
/// <param name="Role">The role that is called, with its model. The provider of the model is the provider of the call.</param>
/// <param name="Request">
/// What the gateway is handed for the call. Its <see cref="AiCallRequest.LiveSearch"/> says
/// whether the model may search live for it.
/// </param>
public sealed record RoleCall(AgentRoleView Role, AiCallRequest Request);

/// <summary>
/// Says what the model of a role is called with. Whether a model can search live is the
/// Catalog's to say, from the capability flags of the configuration file (FR-CAT-6).
/// </summary>
internal sealed class RoleCalls(IModelCapabilities capabilities) : IRoleCalls
{
    public RoleCall Of(AgentRoleView role, ComposedPrompt prompt, string resultSchema)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(prompt);

        // The one role that reads the web, with a model that the file says can.
        var searchesLive = role.Role == Role.SentimentAnalyst && capabilities.SearchesLive(role.Provider, role.Identifier);

        return new RoleCall(role, new AiCallRequest(role.Identifier, prompt.Text, resultSchema, searchesLive));
    }
}
