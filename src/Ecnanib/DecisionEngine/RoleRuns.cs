using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.Platform.Logging;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Runs one role of a team in a cycle: composes its prompt, calls its model with the key of the
/// agent's user, validates what the model answered, and calls once more when the call timed out
/// or the result was invalid (FR-TEAM-6, FR-TEAM-9). Every call is kept for the record of the
/// cycle (FR-TEAM-8). See "A role call" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A model is called with a prompt and a schema, and with nothing that it could call.</b>
/// What it answers is read as data, by <see cref="IRoleResults"/>, into a result with the fields
/// of its schema. Nothing of an answer is carried out (NFR-SEC-6).
/// </para>
/// <para>
/// <b>The key is asked for as each call is made,</b> handed to the gateway with that call, and
/// kept nowhere (FR-KEY-9). A key that the provider refuses is checked again, which marks it and
/// tells what depends on it (FR-KEY-3, FR-KEY-10).
/// </para>
/// </remarks>
internal sealed class RoleRuns(
    IPromptComposition composition,
    IRoleCalls calls,
    IRoleResults results,
    IKeyHandOver keys,
    IKeyValidation keyValidation,
    IAiProviderGateway providers,
    TimeProvider clock,
    ILogger<RoleRuns> logger)
{
    /// <summary>The most calls of one role in a cycle: the call, and the one that is made once more (FR-TEAM-9).</summary>
    public const int Attempts = 2;

    /// <summary>Runs a role, and says what it gave: a valid result, or none.</summary>
    /// <param name="kept">What the cycle keeps of its calls.</param>
    /// <param name="cycle">The cycle.</param>
    /// <param name="role">The role, with its model.</param>
    /// <param name="parts">What the role is given: the parts of its prompt besides the templates.</param>
    /// <param name="read">Validates what the model answered.</param>
    /// <param name="cancellationToken">Canceled when the cycle is stopped.</param>
    /// <exception cref="PromptRefusedException">A part of the prompt holds what a prompt never holds. No call is made.</exception>
    public async Task<RoleRun<T>> RunAsync<T>(
        CycleCalls kept,
        CycleStart cycle,
        AgentRoleView role,
        PromptParts parts,
        Func<string, RoleResult<T>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        // The prompt is composed once, and the call that is made once more is made with the same.
        var prompt = await composition.ComposeAsync(role.Role, parts, cancellationToken);
        var call = calls.Of(role, prompt, results.SchemaOf(role.Role));

        // Every event of the call says which role it is for, the gateway's own among them.
        using var scope = logger.BeginCorrelation(new Correlation(Role: role.Role.ToString()));

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var made = await AttemptAsync(kept, cycle, call, prompt, attempt, read, cancellationToken);

            if (made.Result is { IsValid: true } valid)
            {
                return new RoleRun<T>(call, made.Answer, valid);
            }

            if (!made.MadeAgain)
            {
                break;
            }
        }

        logger.LogWarning(
            "The {Role} of agent {AgentId} gave no valid result for the close at {ClosedAt}", role.Role, cycle.Agent.Id, cycle.ClosedAt);

        return new RoleRun<T>(call, Answer: null, Result: null);
    }

    private async Task<Attempt<T>> AttemptAsync<T>(
        CycleCalls kept,
        CycleStart cycle,
        RoleCall call,
        ComposedPrompt prompt,
        int attempt,
        Func<string, RoleResult<T>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        var role = call.Role;
        var userId = cycle.Agent.UserId;
        var calledAt = clock.GetUtcNow();

        // Decrypted for this call alone, and asked for again for the next (FR-KEY-9).
        var key = await keys.AiKeyAsync(userId, role.Provider, cancellationToken);

        if (key.Key is not { } handed)
        {
            logger.LogWarning(
                "The {Role} of agent {AgentId} is not called: user {UserId} has no key for the AI provider {Provider} that can be used ({KeyHandOverOutcome})",
                role.Role,
                cycle.Agent.Id,
                userId,
                role.Provider.Value,
                key.Outcome);

            kept.Add(OutputOf(cycle, call, prompt, attempt, calledAt, RoleCallOutcome.NoKey, RoleValidation.NoResult));

            return new Attempt<T>(Answer: null, Result: null, MadeAgain: false);
        }

        var began = clock.GetTimestamp();
        AiCallResult answer;

        try
        {
            answer = await providers.CompleteAsync(role.Provider, call.Request, handed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The cycle is stopped while the call is under way. The call is of the record all the same.
            kept.Add(OutputOf(
                cycle, call, prompt, attempt, calledAt, RoleCallOutcome.Canceled, RoleValidation.NoResult, latency: clock.GetElapsedTime(began)));

            throw;
        }

        var outcome = OutcomeOf(answer.Outcome);

        if (answer.Outcome == AiCallOutcome.AuthenticationFailed)
        {
            // The provider refused the key: it is asked about it, which marks a key that it
            // refuses again, and tells what depends on it (FR-KEY-3).
            kept.Revalidate(role.Provider, () => RevalidateAsync(userId, role.Provider, cancellationToken));
        }

        if (answer.ResultJson is not { } answered)
        {
            kept.Add(OutputOf(cycle, call, prompt, attempt, calledAt, outcome, RoleValidation.NoResult, answer));

            // A call that timed out, or that gave an invalid result, is made once more (FR-TEAM-9).
            return new Attempt<T>(answer, Result: null, MadeAgain: answer.Outcome is AiCallOutcome.TimedOut or AiCallOutcome.InvalidResult);
        }

        var result = read(answered);

        kept.Add(OutputOf(
            cycle,
            call,
            prompt,
            attempt,
            calledAt,
            outcome,
            result.IsValid ? RoleValidation.Valid : RoleValidation.Rejected,
            answer,
            result.Rejection?.ToString(),
            result.Json));

        if (result.Rejection is { } rejection)
        {
            // The field and the rule are the service's own words: nothing of the answer is logged.
            logger.LogWarning(
                "The result of the {Role} of agent {AgentId} was rejected on attempt {Attempt}: {RejectedField} is {RejectionRule}",
                role.Role,
                cycle.Agent.Id,
                attempt,
                rejection.Field,
                rejection.Rule);
        }

        // A result that is outside the schema or the ranges is an invalid result.
        return new Attempt<T>(answer, result, MadeAgain: !result.IsValid);
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
            // The cycle is stopped. The key is checked again with the next call that it fails, and daily.
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

    /// <summary>The row of a call: what was called, with what, how it ended, and what was made of its answer.</summary>
    private static RoleOutput OutputOf(
        CycleStart cycle,
        RoleCall call,
        ComposedPrompt prompt,
        int attempt,
        DateTimeOffset calledAt,
        RoleCallOutcome outcome,
        RoleValidation validation,
        AiCallResult? answer = null,
        string? rejection = null,
        string? result = null,
        TimeSpan? latency = null)
    {
        // The four templates of a call, in the order a prompt is composed in: the instructions
        // of the role, and the modules of the market, the timeframe, and the profile.
        var templates = prompt.Templates;

        if (templates.Count != 4)
        {
            throw new InvalidOperationException(
                $"The prompt of a role call is composed from four templates, and this one from {templates.Count}. See docs/prompts.md.");
        }

        return new RoleOutput(
            Guid.CreateVersion7(),
            cycle.CycleId,
            cycle.Agent.UserId,
            call.Role.Role,
            attempt,
            call.Role.ModelId,
            templates[0].Id,
            templates[1].Id,
            templates[2].Id,
            templates[3].Id,
            calledAt,
            (int)Math.Round((answer?.Latency ?? latency ?? TimeSpan.Zero).TotalMilliseconds),
            answer?.Usage?.InputTokens,
            answer?.Usage?.OutputTokens,

            // The searches of a call that offered the model one, which its provider charges for (FR-CYC-3).
            call.Request.LiveSearch ? answer?.LiveSearches : null,
            outcome,
            validation,
            rejection,
            result);
    }

    private static RoleCallOutcome OutcomeOf(AiCallOutcome outcome) => outcome switch
    {
        AiCallOutcome.Succeeded => RoleCallOutcome.Succeeded,
        AiCallOutcome.InvalidResult => RoleCallOutcome.InvalidResult,
        AiCallOutcome.TimedOut => RoleCallOutcome.TimedOut,
        AiCallOutcome.AuthenticationFailed => RoleCallOutcome.AuthenticationFailed,
        AiCallOutcome.ModelNotFound => RoleCallOutcome.ModelNotFound,
        AiCallOutcome.Failed => RoleCallOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "No such outcome of a call."),
    };

    /// <summary>One call of a role: what the gateway answered, what the validation made of it, and whether the role is called once more.</summary>
    private readonly record struct Attempt<T>(AiCallResult? Answer, RoleResult<T>? Result, bool MadeAgain)
        where T : class;
}

/// <summary>What a role gave in a cycle.</summary>
/// <typeparam name="T">The result of the role.</typeparam>
/// <param name="Call">What its model was called with.</param>
/// <param name="Answer">What the gateway answered the call with that gave the valid result, or null when the role gave none.</param>
/// <param name="Result">The valid result, or null when the role gave none, also when it was called once more.</param>
internal sealed record RoleRun<T>(RoleCall Call, AiCallResult? Answer, RoleResult<T>? Result)
    where T : class;

/// <summary>
/// What one cycle keeps until it is recorded: the summary of its data package, each call of its
/// roles, in the order the calls ended in, the checks of the Risk Guard, why it placed no entry,
/// and the checks of keys that the cycle started. Two roles are called at the same time, so it
/// is written to under a lock.
/// </summary>
internal sealed class CycleCalls
{
    private readonly Lock _gate = new();
    private readonly List<RoleOutput> _outputs = [];
    private readonly List<RiskCheckRecord> _guarded = [];
    private readonly HashSet<AiProviderCode> _checked = [];
    private readonly List<Task> _checks = [];
    private DataPackageSummary? _package;
    private NoEntryReason? _noEntry;

    /// <summary>The calls of the cycle so far, in the order they ended in.</summary>
    public IReadOnlyList<RoleOutput> Outputs
    {
        get
        {
            lock (_gate)
            {
                return [.. _outputs];
            }
        }
    }

    /// <summary>What the Risk Guard made of what went on of the plan, in the order of the checks (FR-RSK-9).</summary>
    public IReadOnlyList<RiskCheckRecord> Guarded
    {
        get
        {
            lock (_gate)
            {
                return [.. _guarded];
            }
        }
    }

    /// <summary>
    /// The summary of the data package that the team of the cycle decided on (FR-CYC-10), or
    /// null while none is built: a cycle that ends before it has none.
    /// </summary>
    public DataPackageSummary? Package
    {
        get
        {
            lock (_gate)
            {
                return _package;
            }
        }
    }

    /// <summary>
    /// Why the cycle placed no entry for a plan to open a position that the Risk Guard accepted,
    /// or null: it placed one, or its plan opened nothing.
    /// </summary>
    public NoEntryReason? NoEntry
    {
        get
        {
            lock (_gate)
            {
                return _noEntry;
            }
        }
    }

    /// <summary>Keeps the summary of the data package of the cycle.</summary>
    public void Keep(DataPackageSummary package)
    {
        lock (_gate)
        {
            _package = package;
        }
    }

    /// <summary>Keeps what placing the orders of the cycle said of its entry.</summary>
    public void Keep(NoEntryReason? noEntry)
    {
        lock (_gate)
        {
            _noEntry = noEntry;
        }
    }

    /// <summary>Keeps a call.</summary>
    public void Add(RoleOutput output)
    {
        lock (_gate)
        {
            _outputs.Add(output);
        }
    }

    /// <summary>Keeps a check of the Risk Guard.</summary>
    public void Add(RiskCheckRecord check)
    {
        lock (_gate)
        {
            _guarded.Add(check);
        }
    }

    /// <summary>
    /// Starts the check of the key of a provider, once in the cycle: a provider that holds two
    /// roles refuses the same key twice.
    /// </summary>
    public void Revalidate(AiProviderCode provider, Func<Task> check)
    {
        lock (_gate)
        {
            if (!_checked.Add(provider))
            {
                return;
            }
        }

        var started = check();

        lock (_gate)
        {
            _checks.Add(started);
        }
    }

    /// <summary>Waits for the checks that the cycle started, so that nothing of a cycle goes on after it.</summary>
    public Task SettledAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_checks);
        }
    }
}
