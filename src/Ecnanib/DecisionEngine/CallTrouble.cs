using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Notifications;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What follows when an AI provider keeps refusing the calls with the key of a user: the agent
/// whose call it was moves to Attention required, and its user is told (NFR-REL-4, SRS 3.3). See
/// "Calls that fail repeatedly" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// The gateway pauses the calls with a key that a provider refuses several times in a row, and
/// makes them again later. A call that begins such a pause says how many the key has had in a
/// row, with no answer between them. When that is as many as the configuration file says, the
/// provider is not going to take the key by itself: a limit on its requests that is used up, an
/// account that has run out, or a model that does not take the call. That is for the user to
/// see to, so the agent waits for them.
/// </para>
/// <para>
/// <b>Only trouble of a key moves an agent.</b> A provider that does not answer is paused for
/// everyone, and made again later: its agents go on running, and decide again when it answers.
/// And when many keys of one provider are refused at the same time, the trouble is of the
/// service or of the provider, such as a block of the address that the service calls from: no
/// agent is moved then, and it is said in the log.
/// </para>
/// <para>
/// Each agent is moved by a call of its own. Another agent of the same user that uses the same
/// key goes on until a call of its own cycle is refused, which begins the next pause.
/// </para>
/// </remarks>
internal sealed class CallTrouble(
    IAgentTrading agents,
    INotifications notifications,
    IAiCallPauses pauses,
    BreakersSettings settings,
    ILogger<CallTrouble> logger)
{
    /// <summary>
    /// Takes that a call of an agent's role was refused, and began a pause of the calls with the
    /// key. It does not throw: a cycle ends as it would have without it.
    /// </summary>
    /// <param name="agent">The agent whose cycle made the call.</param>
    /// <param name="role">The role that was called.</param>
    /// <param name="provider">The provider that refused the call.</param>
    /// <param name="keyTrips">How many pauses in a row the calls with the key have had now.</param>
    public async Task SeenAsync(Agent agent, Role role, AiProviderCode provider, int keyTrips)
    {
        try
        {
            if (keyTrips < settings.AiKey.TripsBeforeAttention)
            {
                return;
            }

            var keysInTrouble = pauses.Pauses().FirstOrDefault(pause => pause.Provider == provider)?.KeysInTrouble ?? 0;

            if (keysInTrouble >= settings.AiKey.ProviderWideKeys)
            {
                logger.LogWarning(
                    "The AI provider {Provider} keeps refusing the key of user {UserId} for the {Role} of agent {AgentId}, as it refuses {KeysInTrouble} keys at the same time. That is no trouble of one key, so the agent is not moved",
                    provider.Value,
                    agent.UserId,
                    role,
                    agent.Id,
                    keysInTrouble);

                return;
            }

            // Not canceled with the cycle: what was found is true whether the cycle goes on or not.
            if (!await agents.RequireAttentionAsync(agent.UserId, agent.Id, AgentStateCause.CallsFailRepeatedly, CancellationToken.None))
            {
                // The agent no longer runs: its user paused or stopped it, or something else moved it.
                return;
            }

            // Told when the agent was moved, and not when it was not (FR-NTF-1). Nothing of the
            // key is said, and no provider is named: the role says which key it is.
            await notifications.RaiseAsync(
                agent.UserId,
                NotificationType.AttentionRequired,
                agent.Id,
                agent.Mode,
                $"The agent in {agent.Symbol} ({agent.Mode}) waits for you: the AI provider of its {RoleNames.Listed([role])} keeps " +
                "refusing the calls with your key. That is a limit on the requests of the key, an account at the provider that " +
                "has run out, or a model that does not take the call. See to it at the provider, or give the role another " +
                "model, and resume the agent.",
                CancellationToken.None);
        }
        catch (Exception error)
        {
            // Whatever it is: nothing here is canceled with the cycle, and nothing of it may
            // leave into the cycle, which waits for this before it is recorded.
            logger.LogError(
                error,
                "What follows from the AI provider {Provider} refusing the key of user {UserId} could not be done for agent {AgentId}",
                provider.Value,
                agent.UserId,
                agent.Id);
        }
    }
}
