using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.Experience;
using Ecnanib.Platform.Logging;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Has the model of an agent's Risk Manager write the lesson of a closed position (FR-EXP-6):
/// composes the prompt, calls the model with the key of the agent's user, validates what it
/// wrote, and calls once more when the call timed out or the lesson was invalid, as a role is
/// called once more in a cycle (FR-TEAM-9). See "Writing a lesson" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is beside <see cref="RoleRuns"/>, and is no call of a cycle.</b> A lesson is written
/// after a position is closed, by a job, and belongs to no cycle: so its call is kept in no
/// record of a cycle. What becomes of it is in the row of the lesson.
/// </para>
/// <para>
/// <b>A model is called with a prompt and a schema, and with nothing that it could call,</b> and
/// without a live search. What it answers is read as data, by <see cref="IRoleResults"/>, into
/// a lesson with the fields of its schema. Nothing of an answer is carried out (NFR-SEC-6): a
/// lesson is text that a later prompt names as data.
/// </para>
/// <para>
/// <b>The key is asked for as each call is made,</b> handed to the gateway with that call, and
/// kept nowhere (FR-KEY-9). It is the key of the user whose agent held the position, by the row
/// of the lesson. A key that the provider refuses is checked again, which marks it and tells
/// what depends on it (FR-KEY-3, FR-KEY-10).
/// </para>
/// <para>
/// <b>Every call is billed to that user, so there are two at the most.</b> A lesson that neither
/// gave is not written, and nothing calls for it again.
/// </para>
/// <para>
/// <b>A call that the gateway holds back was not made, and is no call of the two.</b> The calls
/// to the provider, or the calls with the key, are paused after failures in a row (NFR-REL-4).
/// The lesson is left as it is, for a later run to write.
/// </para>
/// </remarks>
internal sealed class LessonRuns(
    IPromptComposition composition,
    IRoleCalls calls,
    IRoleResults results,
    IKeyHandOver keys,
    IKeyValidation keyValidation,
    IAiProviderGateway providers,
    ILogger<LessonRuns> logger)
{
    /// <summary>The most calls for one lesson: the call, and the one that is made once more.</summary>
    public const int Attempts = 2;

    /// <summary>Has a lesson written, and says what came of it: the lesson, or why there is none.</summary>
    /// <param name="userId">The user whose agent held the position, and whose key the model is called with.</param>
    /// <param name="agentId">That agent.</param>
    /// <param name="riskManager">The Risk Manager of the agent, with the model that its user gave it.</param>
    /// <param name="parts">What the call is given: the closed position, and what the team answered when it was opened.</param>
    /// <param name="cancellationToken">Canceled when the run of the job is stopped.</param>
    public async Task<LessonRun> RunAsync(
        Guid userId, Guid agentId, AgentRoleView riskManager, LessonParts parts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(riskManager);
        ArgumentNullException.ThrowIfNull(parts);

        ComposedPrompt prompt;

        try
        {
            // Composed once, and the call that is made once more is made with the same.
            prompt = await composition.ComposeLessonAsync(parts, cancellationToken);
        }
        catch (PromptRefusedException refused)
        {
            // The part and the kind of thing, and never what was found.
            logger.LogWarning(
                "The lesson of a position of agent {AgentId} is not written: its part \"{PromptPart}\" would hold {PromptLeak}",
                agentId,
                refused.Part,
                refused.Holds);

            return new LessonRun(LessonOutcome.NotComposed, Draft: null, TemplateId: null);
        }

        var call = calls.Of(riskManager, prompt, results.LessonSchema);
        var provider = riskManager.Provider;
        var revalidated = false;

        // Every event of the call says which role's model it is for, the gateway's own among them.
        using var scope = logger.BeginCorrelation(new Correlation(Role: riskManager.Role.ToString()));

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            // Decrypted for this call alone, and asked for again for the next (FR-KEY-9).
            var key = await keys.AiKeyAsync(userId, provider, cancellationToken);

            if (key.Key is not { } handed)
            {
                logger.LogWarning(
                    "The lesson of a position of agent {AgentId} is not written: user {UserId} has no key for the AI provider {Provider} that can be used ({KeyHandOverOutcome})",
                    agentId,
                    userId,
                    provider.Value,
                    key.Outcome);

                return new LessonRun(LessonOutcome.NoKey, Draft: null, prompt.Templates[0].Id);
            }

            var answer = await providers.CompleteAsync(provider, call.Request, handed, cancellationToken);

            if (answer.Outcome is AiCallOutcome.ProviderPaused or AiCallOutcome.KeyPaused)
            {
                // Nothing was sent, and nothing was billed: the lesson waits for a run in which
                // the calls are made again. That they are paused was said when the pause began.
                logger.LogInformation(
                    "The lesson of a position of agent {AgentId} is left for a later run: the calls to the AI provider {Provider} are paused ({AiCallOutcome})",
                    agentId,
                    provider.Value,
                    answer.Outcome);

                return new LessonRun(LessonOutcome.NoResult, Draft: null, prompt.Templates[0].Id, Paused: true);
            }

            if (answer.Outcome == AiCallOutcome.AuthenticationFailed && !revalidated)
            {
                // The provider refused the key: it is asked about it, once, which marks a key
                // that it refuses again, and tells what depends on it (FR-KEY-3).
                revalidated = true;
                await RevalidateAsync(userId, provider, cancellationToken);
            }

            if (answer.ResultJson is not { } answered)
            {
                // A call that timed out, or that gave an invalid result, is made once more.
                if (answer.Outcome is AiCallOutcome.TimedOut or AiCallOutcome.InvalidResult)
                {
                    continue;
                }

                break;
            }

            var result = results.ReadLesson(answered);

            if (result.Value is { } draft)
            {
                return new LessonRun(LessonOutcome.Written, draft, prompt.Templates[0].Id);
            }

            if (result.Rejection is { } rejection)
            {
                // The field and the rule are the service's own words: nothing of the answer is logged.
                logger.LogWarning(
                    "The lesson of a position of agent {AgentId} was rejected on attempt {Attempt}: {RejectedField} is {RejectionRule}",
                    agentId,
                    attempt,
                    rejection.Field,
                    rejection.Rule);
            }
        }

        logger.LogWarning("The {Role} of agent {AgentId} gave no valid lesson of a closed position", riskManager.Role, agentId);

        return new LessonRun(LessonOutcome.NoResult, Draft: null, prompt.Templates[0].Id);
    }

    /// <summary>Asks the provider about a key that it refused, and says what came of it. It does not throw.</summary>
    private async Task RevalidateAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await keyValidation.RevalidateAiKeyAsync(userId, provider, cancellationToken);

            logger.LogInformation(
                "The AI provider {Provider} refused the key of user {UserId} in a call, so the key was checked again: {KeyCheckOutcome}",
                provider.Value,
                userId,
                outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run is stopped. The key is checked again with the next call that it fails, and daily.
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "The key of user {UserId} for the AI provider {Provider} could not be checked again after a call that it failed",
                userId,
                provider.Value);
        }
    }
}

/// <summary>What came of having a lesson written.</summary>
/// <param name="Outcome">Written, or why not.</param>
/// <param name="Draft">The lesson, when it is written, and null otherwise.</param>
/// <param name="TemplateId">The version of the lesson instructions that the prompt was composed from, or null when none was composed.</param>
/// <param name="Paused">
/// Whether no call was made because the calls are paused (NFR-REL-4). Nothing came of the lesson
/// then, and its row is left as it is: a later run has it written.
/// </param>
internal sealed record LessonRun(LessonOutcome Outcome, LessonDraft? Draft, int? TemplateId, bool Paused = false);
