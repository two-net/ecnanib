using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;

namespace Ecnanib.WebUi.Pages.Dashboard;

/// <summary>A step of the setup checklist of a first-time user, in the order of the checklist (FR-UI-4).</summary>
public enum SetupStep
{
    /// <summary>Add keys: one of an AI provider, and a Binance key for Demo.</summary>
    Keys,

    /// <summary>Create a Demo agent.</summary>
    Agent,

    /// <summary>Review its decisions.</summary>
    Decisions,

    /// <summary>Then consider Live.</summary>
    Live,
}

/// <summary>
/// Where a user stands on the setup checklist of a first-time user (FR-UI-4): add keys, create a
/// Demo agent, review its decisions, then consider Live. It is worked out from what the
/// dashboard reads, with no database of its own. See "The setup checklist" in docs/dashboard.md.
/// </summary>
/// <remarks>
/// The first two steps say the account as it is now: a key that is deleted is a step that is
/// open again. The last two are what the user said, which stands (<see cref="SetupMarks"/>).
/// </remarks>
/// <param name="AiKeyStored">Whether the user has a key of at least one AI provider, which a team needs.</param>
/// <param name="DemoKeyStored">Whether the user has a Binance key for Demo, which a Demo agent trades with.</param>
/// <param name="AgentCreated">Whether the user has an agent. Every agent is created in Demo (FR-AGT-7).</param>
/// <param name="Decided">
/// The agent whose decisions there are to review, or null while no team of the user has decided:
/// the agent of the latest decision in Demo, or of the latest in Live for a user who has none
/// in Demo.
/// </param>
/// <param name="Marks">What the user said so far.</param>
public sealed record SetupProgress(bool AiKeyStored, bool DemoKeyStored, bool AgentCreated, AgentView? Decided, SetupMarks Marks)
{
    /// <summary>The steps, in the order of the checklist.</summary>
    public static IReadOnlyList<SetupStep> Steps { get; } = [SetupStep.Keys, SetupStep.Agent, SetupStep.Decisions, SetupStep.Live];

    /// <summary>
    /// Whether the checklist is no longer shown: its user said that they reviewed decisions and
    /// considered Live. It stays so whatever they delete later.
    /// </summary>
    public bool Finished => Marks.Both;

    /// <summary>How many of the steps are done.</summary>
    public int Done => Steps.Count(IsDone);

    /// <summary>
    /// Whether the user is offered to mark the decisions as reviewed: there is a decision to
    /// review, and they have not said so yet.
    /// </summary>
    public bool CanMarkReviewed => Decided is not null && !IsDone(SetupStep.Decisions);

    /// <summary>
    /// Whether the user is offered to mark Live as considered: after the review, as the
    /// checklist says "then", and while they have not said so yet.
    /// </summary>
    public bool CanMarkConsidered => IsDone(SetupStep.Decisions) && !IsDone(SetupStep.Live);

    /// <summary>Whether a step is done.</summary>
    public bool IsDone(SetupStep step) => step switch
    {
        SetupStep.Keys => AiKeyStored && DemoKeyStored,
        SetupStep.Agent => AgentCreated,
        SetupStep.Decisions => Marks.DecisionsReviewedAt is not null,
        SetupStep.Live => Marks.LiveConsideredAt is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "There is no such step."),
    };

    /// <summary>Where a user stands, from what the dashboard read of them.</summary>
    /// <param name="keys">The keys that the user has stored.</param>
    /// <param name="agents">The agents of the user.</param>
    /// <param name="demoDecisions">The latest cycles of the user in Demo, latest first.</param>
    /// <param name="liveDecisions">The latest cycles of the user in Live, latest first.</param>
    /// <param name="marks">What the user said so far.</param>
    public static SetupProgress Of(
        StoredKeysState keys,
        IReadOnlyList<AgentView> agents,
        IReadOnlyList<RecentCycle> demoDecisions,
        IReadOnlyList<RecentCycle> liveDecisions,
        SetupMarks marks)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(demoDecisions);
        ArgumentNullException.ThrowIfNull(liveDecisions);
        ArgumentNullException.ThrowIfNull(marks);

        // A decision is a cycle that the close of a candle started: one that its user ran as a
        // test is not among the decisions of an agent (FR-CYC-12). Demo first, as the checklist
        // leads there. An agent that was deleted since its cycle was read has nothing to review.
        var decided = demoDecisions.Concat(liveDecisions)
            .Where(decision => decision.Cycle.Kind == CycleKind.Scheduled)
            .Select(decision => agents.FirstOrDefault(agent => agent.Id == decision.AgentId))
            .FirstOrDefault(agent => agent is not null);

        return new SetupProgress(
            AiKeyStored: keys.AiKeys.Any(slot => slot.Key is not null),
            DemoKeyStored: keys.ExchangeKeys.Any(slot => slot.Environment == TradingMode.Demo && slot.Key is not null),
            AgentCreated: agents.Count > 0,
            Decided: decided,
            Marks: marks);
    }
}
