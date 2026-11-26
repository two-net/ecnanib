using System.Globalization;

namespace Ecnanib.WebUi.Pages.Dashboard;

/// <summary>
/// What the setup checklist of a first-time user says, in one place (NFR-USA-4): its texts, the
/// names of its two forms, and the keys of its steps (FR-UI-4). See "The setup checklist" in
/// docs/dashboard.md.
/// </summary>
public static class SetupPages
{
    /// <summary>The heading of the checklist.</summary>
    public static string Heading => "Getting started";

    /// <summary>The id of that heading, which names the section of the checklist.</summary>
    public const string HeadingId = "setup-heading";

    /// <summary>What the checklist is, and when it goes away.</summary>
    public static string Explained =>
        "Four steps lead from an empty account to a Demo agent whose decisions you have reviewed, and then to your own choice " +
        "about Live. This list goes away once the last two steps are done.";

    /// <summary>What a step that is done says, as a word (FR-THM-5).</summary>
    public static string DoneWord => "Done";

    /// <summary>What a step that is not done says, as a word.</summary>
    public static string NotDoneWord => "Not done yet";

    /// <summary>The text of the link to the API keys.</summary>
    public static string KeysLink => "Go to your API keys";

    /// <summary>The text of the link to the form of a new agent.</summary>
    public static string AgentLink => "Create an agent";

    /// <summary>The text of the link to the agents, where one is switched to Live.</summary>
    public static string AgentsLink => "Go to your agents";

    /// <summary>The text of the link to the risk disclosure.</summary>
    public static string RiskLink => "Read the risk disclosure";

    /// <summary>
    /// The name of the form that marks the decisions as reviewed. Neither name starts as the
    /// name of the form that marks a notification as read does.
    /// </summary>
    public const string ReviewedForm = "setup-reviewed";

    /// <summary>The name of the form that marks Live as considered.</summary>
    public const string ConsideredForm = "setup-considered";

    /// <summary>The button that marks the decisions as reviewed.</summary>
    public static string MarkReviewed => "Mark as reviewed";

    /// <summary>The button that marks Live as considered.</summary>
    public static string MarkConsidered => "I have considered Live";

    /// <summary>How many of the steps are done.</summary>
    public static string Counted(int done)
    {
        var steps = SetupProgress.Steps.Count.ToString(CultureInfo.InvariantCulture);

        return done switch
        {
            0 => $"None of the {steps} steps is done yet.",
            1 => $"1 of the {steps} steps is done.",
            _ => $"{done.ToString(CultureInfo.InvariantCulture)} of the {steps} steps are done.",
        };
    }

    /// <summary>The name of a step.</summary>
    public static string Name(SetupStep step) => step switch
    {
        SetupStep.Keys => "Add keys",
        SetupStep.Agent => "Create a Demo agent",
        SetupStep.Decisions => "Review its decisions",
        SetupStep.Live => "Consider Live",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "There is no such step."),
    };

    /// <summary>Which item of the list a step is, for what moves (FR-ANI-3): see docs/motion.md.</summary>
    public static string Key(SetupStep step) => $"setup-{step.ToString().ToLowerInvariant()}";

    /// <summary>Whether a step is done, as a word.</summary>
    public static string State(bool done) => done ? DoneWord : NotDoneWord;

    /// <summary>The text of the link to the decisions of an agent.</summary>
    public static string DecisionsLink(string agentName) => $"Read the decisions of {agentName}";

    /// <summary>What a step says under its name: what is done of it, or what there is to do.</summary>
    public static string Says(SetupStep step, SetupProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return step switch
        {
            SetupStep.Keys => (progress.AiKeyStored, progress.DemoKeyStored) switch
            {
                (true, true) => "A key of an AI provider and a Binance key for Demo are stored.",
                (true, false) => "A key of an AI provider is stored. Still to add: a Binance key for Demo, which your agent trades with.",
                (false, true) => "A Binance key for Demo is stored. Still to add: a key of an AI provider, for the team of your agent.",
                (false, false) =>
                    "An agent needs a key of an AI provider for its team, and a Binance key for Demo to trade with. Neither is stored yet.",
            },

            SetupStep.Agent => progress.AgentCreated
                ? "You have an agent. Every agent starts in Demo, where no money is at stake."
                : "An agent trades one symbol on one timeframe. It starts in Demo: Binance's test environment, with no real funds.",

            SetupStep.Decisions when progress.IsDone(step) => "You marked the decisions as reviewed.",

            SetupStep.Decisions => progress.Decided is { } agent
                ? $"The team of {agent.Name} has decided. Read what each role answered and how each cycle ended, and then mark this step."
                : "No team of yours has decided anything yet. A team decides each time a candle of its agent closes, once the agent is started.",

            SetupStep.Live when progress.IsDone(step) => "You considered Live.",

            SetupStep.Live => progress.CanMarkConsidered
                ? "In Live an agent trades in your real Binance account, with real money, and can lose it. Switching an agent to " +
                  "Live takes a valid Binance key for Live, your confirmation of the risk, and a fresh proof of who you are. " +
                  "Staying in Demo is a choice too: mark this step either way."
                : "This step comes after you have reviewed decisions.",

            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "There is no such step."),
        };
    }
}
