using System.Globalization;
using Ecnanib.DecisionEngine;

namespace Ecnanib.WebUi.Pages.Usage;

/// <summary>
/// The texts of the page of the AI usage, in one place (NFR-USA-4), how its figures are
/// written, and how its address is read. The sentence of the usage of a key on the page of the
/// keys is written with the same figures. See "AI usage" in docs/team.md.
/// </summary>
public static class UsagePages
{
    /// <summary>The heading of the page, and the text of the link in the bar at the top of every page.</summary>
    public static string Title => "AI usage";

    /// <summary>The text of the link that leads from a page of an agent to its usage.</summary>
    public static string OfAgentLink => "AI usage of the agent";

    /// <summary>The text of the link that leads from the usage of one agent to that of every agent.</summary>
    public static string OfEveryAgentLink => "AI usage of all your agents";

    /// <summary>The text of the link that leads from the usage of a key to the usage by day.</summary>
    public static string ByDayLink => "AI usage by day, agent, and provider";

    /// <summary>
    /// What is written in place of a figure that is not known: the tokens of calls that no
    /// provider counted, and the cost of tokens that have no price. Nothing is guessed, and
    /// nothing is left blank or written as zero: either would read as "none".
    /// </summary>
    public static string Unknown => "Unknown";

    /// <summary>What the page shows, and whose account the calls are charged to (FR-RPT-3).</summary>
    public static string Explained =>
        "These are the calls that the roles of your agents made to AI models: how many there were, how many tokens their " +
        "providers counted for them, and what that is estimated to cost. " +
        "Every call is made with your own key, and is charged to your own account with its provider.";

    /// <summary>Which calls are counted, and which are not.</summary>
    public static string Counted =>
        "Every call that was made is counted, in a test cycle as in any other, and in Demo as in Live: a provider charges " +
        "the same for each. The calls that write lessons are not recorded, and are not counted here. A day is a UTC day.";

    /// <summary>What "Not counted" says, and what the tokens are of.</summary>
    public static string NotCountedExplained =>
        "\"Not counted\" is how many of the calls their provider counted no tokens for, because the call timed out or failed. " +
        "A provider may charge for such a call all the same. The tokens and the estimated cost are of the other calls.";

    /// <summary>What the estimated cost is, and what it is not (FR-CAT-11).</summary>
    public static string CostExplained =>
        "The cost is an estimate, in US dollars: the tokens at the price of each model, as this service is told that price " +
        "now. Where a model has no price here the cost is \"Unknown\", which is not the same as nothing. " +
        "What a provider charges for live searches is not in it. What you are charged is on your account with the provider.";

    /// <summary>That nothing of the usage is kept apart from the calls.</summary>
    public static string Follows =>
        "The usage is worked out from the recorded calls, each time it is shown. When an agent is deleted, its calls go " +
        "with it, and so does its usage.";

    /// <summary>The heading of the filter of the usage.</summary>
    public static string FilterHeading => "Which calls are counted";

    /// <summary>The name of the choice of an agent, in the filter.</summary>
    public static string AgentLabel => "Agent";

    /// <summary>The choice of the filter that names no agent: the page then shows the usage of every agent.</summary>
    public static string AllAgents => "All your agents";

    /// <summary>The name of the choice of a period, in the filter.</summary>
    public static string PeriodLabel => "Period";

    /// <summary>The button that sends the filter: the page then shows what was chosen.</summary>
    public static string Show => "Show";

    /// <summary>The heading of the usage of each provider.</summary>
    public static string ByProviderHeading => "By provider";

    /// <summary>The name of the table of the usage of each provider.</summary>
    public static string ProvidersCaption => "The AI usage of each provider";

    /// <summary>The heading of the usage of each day, agent, and provider.</summary>
    public static string ByDayHeading => "By day, agent, and provider";

    /// <summary>The name of the table of the usage of each day, agent, and provider.</summary>
    public static string DaysCaption => "The AI usage of each day, agent, and provider";

    /// <summary>The heading of the column of the UTC day that some calls were made on.</summary>
    public static string DayHeading => "Day";

    /// <summary>The heading of how many calls were made.</summary>
    public static string CallsHeading => "Calls";

    /// <summary>The heading of how many of the calls their provider counted no tokens for.</summary>
    public static string NotCountedHeading => "Not counted";

    /// <summary>The heading of the tokens that went into the models.</summary>
    public static string TokensInHeading => "Tokens in";

    /// <summary>The heading of the tokens that came out of the models.</summary>
    public static string TokensOutHeading => "Tokens out";

    /// <summary>The heading of how many live searches the models ran.</summary>
    public static string LiveSearchesHeading => "Live searches";

    /// <summary>The heading of what the tokens are estimated to cost.</summary>
    public static string CostHeading => "Estimated cost";

    /// <summary>The name of an agent that was deleted since its calls were read: a cell never says nothing.</summary>
    public static string DeletedAgent => "An agent that was deleted";

    /// <summary>The text of the link that leads from the usage of one agent to the page of its state.</summary>
    public static string AgentStateLink => "State of the agent";

    /// <summary>The text of the link that leads from the page to the API keys.</summary>
    public static string KeysLink => "Your API keys";

    /// <summary>The text of the link that leads from the page to the agents.</summary>
    public static string AgentsLink => "Your agents";

    /// <summary>The heading of the page of the usage of one agent.</summary>
    public static string TitleOf(string agentName) => $"AI usage of {agentName}";

    /// <summary>A period as a person reads it.</summary>
    public static string Describe(UsagePeriod period) => $"Last {Days(period)} days";

    /// <summary>A period as an address writes it: its number of days.</summary>
    public static string Token(UsagePeriod period) => Days(period).ToString(CultureInfo.InvariantCulture);

    /// <summary>Which days the page shows.</summary>
    public static string PeriodExplained(AiUsageView view, AiUsageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(filter);

        return $"The last {Whole(filter.Days)} days: from {PageTimes.UtcDay(view.From)} until now.";
    }

    /// <summary>What the page says in place of its tables when no call was made.</summary>
    /// <param name="ofOneAgent">Whether the page shows the usage of one agent alone.</param>
    public static string NoCalls(bool ofOneAgent) => ofOneAgent
        ? "This agent made no call to an AI model in these days."
        : "Your agents made no call to an AI model in these days.";

    /// <summary>A number of calls or of live searches. It has no separator, as the tokens of a call have none.</summary>
    public static string Whole(long count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>A number of tokens, or that it is not known: no call was counted.</summary>
    public static string Tokens(long? tokens) => tokens is { } counted ? Whole(counted) : Unknown;

    /// <summary>
    /// An estimated cost in US dollars, or that it is not known. A cost is written to the cent,
    /// and one of less than a cent to a hundredth of a cent, so that a cost that is not nothing
    /// is never written as nothing.
    /// </summary>
    public static string Cost(decimal? cost)
    {
        if (cost is not { } estimated)
        {
            return Unknown;
        }

        if (estimated == 0m || estimated >= 0.01m)
        {
            return $"{Rounded(estimated, 2).ToString("0.00", CultureInfo.InvariantCulture)} USD";
        }

        var small = Rounded(estimated, 4);

        return small == 0m ? "less than 0.0001 USD" : $"{small.ToString("0.0000", CultureInfo.InvariantCulture)} USD";
    }

    /// <summary>
    /// The figures of some calls in one sentence, for where they stand outside a table: on the
    /// page of the keys, under the key that they are of.
    /// </summary>
    public static string Sentence(UsageFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);

        if (figures.Calls == 0)
        {
            return "No call was made.";
        }

        var calls = figures.Calls == 1 ? "1 call" : $"{Whole(figures.Calls)} calls";
        var notCounted = figures.NotCounted switch
        {
            0 => string.Empty,
            1 => ", of which 1 was not counted by the provider",
            var some => $", of which {Whole(some)} were not counted by the provider",
        };
        var tokens = figures is { InputTokens: { } input, OutputTokens: { } output }
            ? $"{Whole(input)} tokens in and {Whole(output)} tokens out"
            : "tokens unknown";
        var searches = figures.LiveSearches switch
        {
            0 => string.Empty,
            1 => " 1 live search.",
            var ran => $" {Whole(ran)} live searches.",
        };
        var cost = figures.EstimatedCost is null ? "Estimated cost unknown." : $"Estimated cost {Cost(figures.EstimatedCost)}.";

        return $"{calls}{notCounted}: {tokens}.{searches} {cost}";
    }

    /// <summary>
    /// What the address of the page asks for: the usage of one agent, or of every agent, and
    /// over which period. Null for an address that names a choice which there is not: an agent
    /// that is no id, a period that is none of the periods, or either of them twice. A choice
    /// that is absent or empty is not made. So the page never shows something else than what
    /// its address asks for: it is not found instead.
    /// </summary>
    /// <param name="query">What the address says after its path.</param>
    public static AiUsageFilter? ChoicesOf(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var agent = query[WebUiModule.UsageAgentKey];
        var days = query[WebUiModule.UsagePeriodKey];

        // A choice that is made twice is none that there is.
        if (agent.Count > 1 || days.Count > 1)
        {
            return null;
        }

        Guid? agentId = null;
        var period = AiUsageFilter.None.Period;

        // A choice that is absent or empty is not made.
        if (agent is [{ Length: > 0 } named])
        {
            // As the page itself writes an id, and in no other form of one.
            if (!Guid.TryParseExact(named, "D", out var id) || id.ToString() != named)
            {
                return null;
            }

            agentId = id;
        }

        if (days is [{ Length: > 0 } written])
        {
            // Each period is matched as it is written: `30`, and neither `030` nor `Last30Days`.
            var chosen = Enum.GetValues<UsagePeriod>().Cast<UsagePeriod?>().SingleOrDefault(one => Token(one!.Value) == written);

            if (chosen is null)
            {
                return null;
            }

            period = chosen.Value;
        }

        return new AiUsageFilter(agentId, period);
    }

    /// <summary>The path of the page with a filter, which a link there keeps.</summary>
    public static string Path(AiUsageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var choices = new List<string>();

        if (filter.AgentId is { } agent)
        {
            choices.Add($"{WebUiModule.UsageAgentKey}={agent}");
        }

        // The period that the page shows first is not said.
        if (filter.Period != AiUsageFilter.None.Period)
        {
            choices.Add($"{WebUiModule.UsagePeriodKey}={Token(filter.Period)}");
        }

        return choices.Count == 0 ? WebUiModule.UsagePath : $"{WebUiModule.UsagePath}?{string.Join('&', choices)}";
    }

    private static int Days(UsagePeriod period) => new AiUsageFilter(AgentId: null, period).Days;

    private static decimal Rounded(decimal number, int places) => Math.Round(number, places, MidpointRounding.AwayFromZero);
}
