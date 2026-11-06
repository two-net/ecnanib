using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// Asks one role: its model first, then its fallback (FR-ROLE-06), each on one of the user's own valid keys that the
/// catalog says can use it. The adapter retries per its provider's settings (EXT-AI-03), and the contract check sends
/// a broken answer back once (FR-CYC-05), so a model has failed once either gives up. See Decision cycle in
/// docs/architecture.md.
/// </summary>
internal sealed class Council(
    IPrompts prompts,
    ICatalog catalog,
    ICredentialKeys keys,
    IAiCompletions completions,
    TimeProvider time,
    ILogger<Council> logger) : ICouncil
{
    /// <summary>
    /// Room for an answer, and for the reasoning that providers count as output, below any model's own limit. A
    /// contract's answer is a few hundred tokens.
    /// </summary>
    public const int MaxOutputTokens = 8192;

    public async Task<RoleAnswer> AskAsync(RoleQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);

        var role = question.Role;
        ComposedPrompt composed;

        try
        {
            composed = await prompts.ComposeAsync(question.Prompt, cancellationToken);
        }
        catch (PromptTemplateMissingException exception)
        {
            // No model can help, so the fallback isn't tried.
            logger.LogError(exception, "The prompt for {Role} couldn't be composed, because a template is missing", role);

            return new RoleAnswer(role, null, [NotSent(role, question.Model, fallback: false, CouncilFailure.TemplateMissing, null)]);
        }

        var models = await catalog.ListAsync(cancellationToken);
        var access = await catalog.ListAccessAsync(question.UserId, cancellationToken);
        var credentials = await keys.ListAiAsync(question.UserId, cancellationToken);
        var calls = new List<CouncilCall>();

        List<(CouncilModel Model, bool Fallback)> tries = [(question.Model, false)];

        if (question.Fallback is { } fallback && fallback != question.Model)
        {
            tries.Add((fallback, true));
        }

        foreach (var (model, isFallback) in tries)
        {
            var output = await AskModelAsync(question, composed, model, isFallback, models, access, credentials, calls, cancellationToken);

            if (output is not null)
            {
                return new RoleAnswer(role, output, calls);
            }

            logger.LogWarning(
                "{Role} failed on {Provider} {Model}{Next}",
                role,
                model.Provider,
                model.ModelId,
                !isFallback && tries.Count > 1 ? ", so its fallback model is next" : "");
        }

        return new RoleAnswer(role, null, calls);
    }

    private async Task<RoleOutput?> AskModelAsync(
        RoleQuestion question,
        ComposedPrompt composed,
        CouncilModel model,
        bool fallback,
        IReadOnlyList<CatalogModel> models,
        IReadOnlyList<ModelAccess> access,
        IReadOnlyList<AiCredential> credentials,
        List<CouncilCall> calls,
        CancellationToken cancellationToken)
    {
        var role = question.Role;

        // An agent stores the model, not the key, so the key is the oldest valid one the catalog says can use it.
        var usable = access
            .Where(row => row.Available && row.Provider == model.Provider && row.ModelId == model.ModelId)
            .Select(row => row.CredentialId)
            .ToHashSet();
        var credential = credentials.FirstOrDefault(
            row => row.Status == CredentialStatus.Valid && row.Provider == model.Provider && usable.Contains(row.Id));

        if (credential is null)
        {
            calls.Add(NotSent(role, model, fallback, CouncilFailure.NoKey, composed));
            return null;
        }

        var entry = models.FirstOrDefault(row => row.Provider == model.Provider && row.ModelId == model.ModelId);
        var request = new AiRequest(
            model.ModelId,
            role,
            composed.Instructions,
            [AiMessage.User(composed.Message)],
            Math.Min(MaxOutputTokens, entry?.MaxOutputTokens is > 0 and var limit ? limit : MaxOutputTokens))
        {
            Price = entry?.Price is { } price ? new AiModelPrice(price.InputPerMillion, price.OutputPerMillion) : null,
            LiveSearch = role == AiRole.SentimentAnalyst && entry?.Capabilities.LiveSearch == true,
        };

        var started = time.GetUtcNow();
        var clock = time.GetTimestamp();
        AiKey key;

        try
        {
            key = await keys.OpenAiKeyAsync(question.UserId, credential.Id, cancellationToken);
        }
        catch (CredentialUnusableException exception)
        {
            // The credential changed since the agent was activated. Its checks pause the agent: it's not the provider's failure.
            logger.LogWarning("The key for {Role} on {Provider} can't be used: {Reason}", role, model.Provider, exception.Reason);
            calls.Add(NotSent(role, model, fallback, CouncilFailure.KeyUnusable, composed));
            return null;
        }

        try
        {
            var answer = await OutputContracts.CompleteAsync(
                (sent, token) => completions.CompleteAsync(key, sent, token),
                request,
                ContractContext.For(question.Prompt, question.Proposal),
                logger,
                cancellationToken);

            // Only the last attempt conforms: the one before it, if any, broke the contract.
            calls.AddRange(answer.Attempts.Select((attempt, index) => FromAttempt(
                role, model, fallback, index + 1, attempt, index == answer.Attempts.Count - 1 ? answer.Output : null, composed, started)));

            return answer.Output;
        }
        catch (AiException exception)
        {
            calls.Add(FromFailure(role, model, fallback, 1, exception, time.GetElapsedTime(clock), composed, started));
        }
        catch (OutputContractException exception)
        {
            calls.AddRange(exception.Attempts.Select((attempt, index) => FromAttempt(role, model, fallback, index + 1, attempt, null, composed, started)));

            if (exception.RetryFailure is { } failure)
            {
                var latency = time.GetElapsedTime(clock) - exception.Attempts.Aggregate(TimeSpan.Zero, (sum, attempt) => sum + attempt.Response.Latency);
                calls.Add(FromFailure(
                    role, model, fallback, exception.Attempts.Count + 1, failure, latency > TimeSpan.Zero ? latency : TimeSpan.Zero, composed, started));
            }
        }
        finally
        {
            key.ApiKey.Dispose();
        }

        return null;
    }

    private static CouncilCall FromAttempt(
        AiRole role,
        CouncilModel model,
        bool fallback,
        int attempt,
        ContractAttempt contract,
        RoleOutput? output,
        ComposedPrompt composed,
        DateTimeOffset started)
    {
        var response = contract.Response;

        return new CouncilCall(
            role,
            model,
            fallback,
            attempt,
            output is null ? CouncilCallStatus.BrokeContract : CouncilCallStatus.Answered,
            null,
            null,
            composed.Instructions,
            composed.Message,
            response.Text,
            output,
            contract.Violations,
            response.Usage.InputTokens,
            response.Usage.OutputTokens,
            response.Cost,
            response.Latency,
            response.Attempts,
            response.Truncated,
            composed.Templates,
            started);
    }

    private static CouncilCall FromFailure(
        AiRole role,
        CouncilModel model,
        bool fallback,
        int attempt,
        AiException exception,
        TimeSpan latency,
        ComposedPrompt composed,
        DateTimeOffset started) =>
        new(
            role,
            model,
            fallback,
            attempt,
            CouncilCallStatus.Failed,
            FailureOf(exception.Failure),
            exception.ProviderMessage,
            composed.Instructions,
            composed.Message,
            null,
            null,
            [],
            exception.Usage.InputTokens,
            exception.Usage.OutputTokens,
            exception.Cost,
            latency,
            exception.Attempts,
            false,
            composed.Templates,
            started);

    private CouncilCall NotSent(AiRole role, CouncilModel model, bool fallback, CouncilFailure failure, ComposedPrompt? composed) =>
        new(
            role,
            model,
            fallback,
            1,
            CouncilCallStatus.NotSent,
            failure,
            null,
            composed?.Instructions,
            composed?.Message,
            null,
            null,
            [],
            0,
            0,
            null,
            TimeSpan.Zero,
            0,
            false,
            composed?.Templates ?? [],
            time.GetUtcNow());

    private static CouncilFailure FailureOf(AiFailure failure) => failure switch
    {
        AiFailure.Authentication => CouncilFailure.Authentication,
        AiFailure.RateLimit => CouncilFailure.RateLimit,
        AiFailure.Overloaded => CouncilFailure.Overloaded,
        AiFailure.InvalidRequest => CouncilFailure.InvalidRequest,
        AiFailure.Refusal => CouncilFailure.Refusal,
        AiFailure.Timeout => CouncilFailure.Timeout,
        _ => CouncilFailure.Other,
    };
}
