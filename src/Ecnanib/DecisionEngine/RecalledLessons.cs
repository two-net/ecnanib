using Ecnanib.Agents;
using Ecnanib.Experience;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Hands a cycle the lessons that the prompts of its Strategist and its Risk Manager recall
/// (FR-EXP-7). Experience says which: the lessons of the agent first, then those of the user's
/// other agents on the same symbol, market, and timeframe, up to a count and within a size
/// budget. See "Recalling lessons" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>They are asked for the user of the agent, and for nobody else.</b> A cycle is of one
/// agent, and the user is the one of that agent: a lesson of another user is not recalled.
/// </para>
/// <para>
/// <b>A lesson that holds what a prompt never holds is left out,</b> and the others go on. A
/// lesson is screened when it is written, so none should: but what the screen finds can change
/// after a lesson was stored, with a secret of the configuration file, and a prompt that held
/// such a lesson would be refused (FR-PRM-9). So a cycle is never given up for a stored lesson.
/// </para>
/// <para>
/// <b>Lessons that cannot be read are not waited for.</b> The team decides without them, and
/// the log says so: a cycle is not given up for what would only have helped it.
/// </para>
/// </remarks>
internal sealed class RecalledLessons(ILessons lessons, PromptScreen screen, ILogger<RecalledLessons> logger)
{
    /// <summary>The lessons that the prompts of a cycle of an agent recall, in the order a prompt numbers them in.</summary>
    /// <param name="agent">The agent, as the cycle has it.</param>
    /// <param name="cancellationToken">Canceled when the cycle is stopped.</param>
    public async Task<IReadOnlyList<string>> OfAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        IReadOnlyList<string> recalled;

        try
        {
            recalled = await lessons.RecallAsync(
                agent.UserId, new LessonScope(agent.Id, agent.Symbol, agent.Market, agent.Timeframe), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The lessons of agent {AgentId} could not be recalled, so its team decides with none", agent.Id);

            return [];
        }

        List<string> placed = [.. recalled.Where(lesson => screen.LeakIn(lesson) is null)];

        if (placed.Count < recalled.Count)
        {
            // How many, and never what was found in one.
            logger.LogWarning(
                "{Lessons} lessons of agent {AgentId} are recalled into no prompt: each holds what a prompt never holds",
                recalled.Count - placed.Count,
                agent.Id);
        }

        return placed;
    }
}
