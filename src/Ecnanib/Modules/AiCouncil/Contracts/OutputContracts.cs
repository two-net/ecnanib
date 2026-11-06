using System.Globalization;
using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Modules.AiCouncil.Contracts;

/// <summary>
/// The roles' output contracts (Appendix B): their schemas, which providers enforce where they can (FR-PRM-08), the
/// check every answer gets, and the retry-once call (FR-CYC-05). Nothing here reads anything but its arguments, and
/// nothing calls a provider itself: the council passes <c>IAiCompletions.CompleteAsync</c>, which only AiCouncil may
/// use. See Output contracts in docs/architecture.md.
/// </summary>
public static class OutputContracts
{
    /// <summary>The most violations reported, and sent back in a retry, so a broken answer can't flood the prompt.</summary>
    public const int MaxViolations = 20;

    /// <summary>The longest summary the analysts may write (Appendix B).</summary>
    public const int SummaryMaxLength = 600;

    /// <summary>
    /// The schema for <paramref name="role"/>'s answer on <paramref name="market"/>, for <see cref="AiRequest.Schema"/>.
    /// Only the Strategist's differs by market, in the actions it allows.
    /// </summary>
    public static AiOutputSchema SchemaFor(AiRole role, Market market) => ContractSchemas.For(role, market);

    /// <summary>
    /// Reads <paramref name="answer"/> as <paramref name="role"/>'s output, and returns it or every violation, the
    /// first <see cref="MaxViolations"/> of them. A violation names a path and a rule, and never quotes the answer.
    /// </summary>
    /// <param name="truncated">The model hit the output limit (<see cref="AiResponse.Truncated"/>), which is a violation of itself.</param>
    public static ContractCheck Check(AiRole role, string answer, ContractContext context, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(context);

        return ContractReader.Read(role, answer, context, truncated);
    }

    /// <summary>
    /// <paramref name="output"/> as JSON in its contract's names, which <see cref="Check"/> reads back as the same
    /// output. It's how a validated output is stored in the trace and given to the next roles, rather than the raw
    /// answer, which may hold what the check cleared.
    /// </summary>
    public static string Write(RoleOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return ContractReader.Write(output);
    }

    /// <summary>
    /// Sends <paramref name="request"/> with its role's schema, and checks the answer. An answer that breaks the
    /// contract is sent back once, as two more turns after the composed message: the answer, then the violations
    /// (FR-CYC-05). A second violation means the role failed (FR-ROLE-06), and so does a retry that fails at the
    /// provider. Each violation is logged, with its path and never the answer.
    /// </summary>
    /// <param name="send">Sends one request, such as <c>IAiCompletions.CompleteAsync</c> with the role's key.</param>
    /// <exception cref="AiException">The first call failed after its retries. Its usage and cost are its own.</exception>
    /// <exception cref="OutputContractException">
    /// Both answers broke the contract, or the retry failed at the provider. Either way, it holds every billed attempt.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public static async Task<ContractedAnswer> CompleteAsync(
        Func<AiRequest, CancellationToken, Task<AiResponse>> send,
        AiRequest request,
        ContractContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        var first = request with { Schema = SchemaFor(request.Role, context.Market) };
        var firstResponse = await send(first, cancellationToken);
        var firstAttempt = Attempt(firstResponse, first, context, logger, 1);

        if (firstAttempt.Output is { } output)
        {
            return new ContractedAnswer(output, [firstAttempt.Attempt]);
        }

        var retry = first with { Messages = Retry(first.Messages, firstResponse.Text, firstAttempt.Attempt.Violations) };
        AiResponse retryResponse;

        try
        {
            retryResponse = await send(retry, cancellationToken);
        }
        catch (AiException exception)
        {
            throw new OutputContractException(request.Role, [firstAttempt.Attempt], exception);
        }

        var retryAttempt = Attempt(retryResponse, retry, context, logger, 2);

        return retryAttempt.Output is { } retried
            ? new ContractedAnswer(retried, [firstAttempt.Attempt, retryAttempt.Attempt])
            : throw new OutputContractException(request.Role, [firstAttempt.Attempt, retryAttempt.Attempt]);
    }

    private static (RoleOutput? Output, ContractAttempt Attempt) Attempt(
        AiResponse response,
        AiRequest request,
        ContractContext context,
        ILogger logger,
        int attempt)
    {
        var check = Check(request.Role, response.Text, context, response.Truncated);

        if (check.Output is null)
        {
            logger.LogWarning(
                "AI {Model} for {Role} broke its output contract on attempt {ContractAttempt}: {ViolationCount} violations at {ViolationPaths}",
                request.Model,
                request.Role,
                attempt,
                check.Violations.Count,
                string.Join(", ", check.Violations.Select(violation => violation.Path).Distinct(StringComparer.Ordinal)));
        }

        return (check.Output, new ContractAttempt(response, check.Violations));
    }

    /// <summary>
    /// The conversation so far, then the model's answer, then what was wrong with it. A blank answer can't be a turn,
    /// because providers refuse an empty one, so the violations join the last user message instead.
    /// </summary>
    private static IReadOnlyList<AiMessage> Retry(IReadOnlyList<AiMessage> messages, string answer, IReadOnlyList<ContractViolation> violations)
    {
        var text = new AiText(RetryText(violations));

        if (!string.IsNullOrWhiteSpace(answer))
        {
            return [.. messages, AiMessage.Assistant(answer), AiMessage.User(text)];
        }

        return messages[^1] is { Speaker: AiSpeaker.User } last
            ? [.. messages.SkipLast(1), new AiMessage(AiSpeaker.User, [.. last.Content, text])]
            : [.. messages, AiMessage.User(text)];
    }

    private static string RetryText(IReadOnlyList<ContractViolation> violations)
    {
        var text = new StringBuilder("Your answer didn't follow your output contract:\n\n");

        foreach (var violation in violations)
        {
            text.Append(CultureInfo.InvariantCulture, $"- {(violation.Path == "$" ? "Your answer" : $"`{violation.Path}`")} {violation.Problem}.\n");
        }

        return text
            .Append("\nAnswer again, following the output contract in your instructions: one JSON object and nothing else, ")
            .Append("with no text before or after it and no code fence.")
            .ToString();
    }
}

/// <summary>
/// What a check of an answer needs to know beyond the answer: the market, whose actions the Strategist may propose,
/// and the limits and proposal that some rules compare against. Build it with <see cref="For"/>, from the request
/// the prompt was composed from, so the prompt and the check see the same limits.
/// </summary>
public sealed record ContractContext
{
    public ContractContext(Market market)
    {
        Market = Enum.IsDefined(market) ? market : throw new ArgumentOutOfRangeException(nameof(market), market, "Not a market.");
    }

    public Market Market { get; }

    /// <summary>The agent's maximum risk per trade after the caps, which the Strategist's risk percent can't exceed. Null skips that check.</summary>
    public decimal? MaxRiskPercent { get; init; }

    /// <summary>The side of the agent's open position, for a Risk Critic's changes to a proposal that isn't an entry.</summary>
    public PositionSide? PositionSide { get; init; }

    /// <summary>The Strategist's validated proposal, which the Risk Critic's changes must make less risky.</summary>
    public TradeProposal? Proposal { get; init; }

    /// <summary>The context for a role's answer to the prompt that <paramref name="request"/> composed.</summary>
    /// <param name="proposal">For the Risk Critic, the proposal it judged.</param>
    public static ContractContext For(PromptRequest request, TradeProposal? proposal = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ContractContext(request.Market)
        {
            MaxRiskPercent = request.Context.Risk?.MaxRiskPerTradePercent,
            PositionSide = request.Context.Position?.Side,
            Proposal = proposal,
        };
    }
}

/// <summary>One way an answer broke its contract. It holds no text the model wrote, so it can be logged and sent back.</summary>
/// <param name="Path">Where, as a JSON path such as <c>$.signals[2].strength</c>, or <c>$</c> for the whole answer.</param>
/// <param name="Problem">What's wrong, written to follow the path, such as "must be from 0 to 1".</param>
public sealed record ContractViolation(string Path, string Problem);

/// <summary>An answer's check: its output when it conforms, or its violations when it doesn't.</summary>
public sealed record ContractCheck(RoleOutput? Output, IReadOnlyList<ContractViolation> Violations)
{
    public bool Conforms => Output is not null;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(ContractCheck)}, {(Conforms ? "conforms" : $"{Violations.Count} violations")}");
}

/// <summary>One billed call and what was wrong with its answer, which is nothing for the one that conformed.</summary>
public sealed record ContractAttempt(AiResponse Response, IReadOnlyList<ContractViolation> Violations)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(ContractAttempt)}, {Response}, {Violations.Count} violations");
}

/// <summary>A role's output that conformed, and every call it took, which the decision trace records (FR-PRM-03).</summary>
public sealed record ContractedAnswer(RoleOutput Output, IReadOnlyList<ContractAttempt> Attempts)
{
    /// <summary>Every call's tokens added up, because each was billed.</summary>
    public AiUsage Usage => ContractBilling.Usage(Attempts);

    /// <summary>Every call's cost, or null when the request had no price.</summary>
    public decimal? Cost => ContractBilling.Cost(Attempts);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(ContractedAnswer)} {Output.Role}, {Attempts.Count} attempts");
}

/// <summary>
/// A role's answer broke its output contract twice, or broke it once and the retry failed at the provider, which is
/// <see cref="Exception.InnerException"/>. Either way, the role failed (FR-ROLE-06), and its fallback model is next.
/// The message holds only the role.
/// </summary>
public sealed class OutputContractException : Exception
{
    public OutputContractException(AiRole role, IReadOnlyList<ContractAttempt> attempts, AiException? retryFailure = null)
        : base(
            retryFailure is null
                ? $"The {role}'s answer broke its output contract twice."
                : $"The {role}'s answer broke its output contract, and the retry failed at {retryFailure.Provider}.",
            retryFailure)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        Role = role;
        Attempts = attempts;
    }

    public AiRole Role { get; }

    /// <summary>Every call that answered, each with its violations.</summary>
    public IReadOnlyList<ContractAttempt> Attempts { get; }

    /// <summary>The retry's failure at the provider, when that's how it ended.</summary>
    public AiException? RetryFailure => InnerException as AiException;

    /// <summary>Every call's tokens, the failed retry's included, because each was billed.</summary>
    public AiUsage Usage => ContractBilling.Usage(Attempts) + (RetryFailure?.Usage ?? AiUsage.Zero);

    /// <summary>Every call's cost, the failed retry's included, or null when the request had no price.</summary>
    public decimal? Cost => ContractBilling.Cost(Attempts, RetryFailure?.Cost);
}

file static class ContractBilling
{
    public static AiUsage Usage(IReadOnlyList<ContractAttempt> attempts) =>
        attempts.Aggregate(AiUsage.Zero, (total, attempt) => total + attempt.Response.Usage);

    public static decimal? Cost(IReadOnlyList<ContractAttempt> attempts, decimal? more = null)
    {
        var costs = attempts.Select(attempt => attempt.Response.Cost).Append(more).Where(cost => cost is not null).ToList();

        return costs.Count == 0 ? null : costs.Sum();
    }
}
