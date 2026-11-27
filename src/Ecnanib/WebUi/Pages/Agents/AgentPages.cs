using System.Globalization;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Settings;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Account;
using Ecnanib.WebUi.Pages.Experience;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// The texts of the pages of a user's agents, in one place (NFR-USA-4): what the list of the
/// agents, the form of the settings of an agent, and the page of its prompts say, and what the
/// pages of an agent have in common. That is the names of their forms, how a setting is
/// written, what they say about a setting that cannot be stored, what they say about the state
/// of an agent and the controls that change it (FR-AGT-1, FR-AGT-2, FR-AGT-10), what they say
/// about its mode and about switching it (FR-AGT-7, FR-UI-3), and what they say about the
/// prompts that it uses (FR-PRM-7). Every setting and every state is explained in plain
/// language (NFR-USA-2). The texts of the pages that say and change the state of an agent are
/// in <see cref="AgentStatePages"/>, and those of the two pages that switch its mode in
/// <see cref="AgentModePages"/>.
/// </summary>
public static class AgentPages
{
    /// <summary>The name of the form that creates an agent, or changes one.</summary>
    public const string SaveForm = "save-agent";

    /// <summary>The name of the form that deletes an agent.</summary>
    public const string DeleteForm = "delete-agent";

    /// <summary>The name of the form that stops an agent.</summary>
    public const string StopForm = "stop-agent";

    /// <summary>The name of the form that closes the open position of an agent.</summary>
    public const string ClosePositionForm = "close-agent-position";

    /// <summary>The name of the form that runs a test cycle of an agent (FR-CYC-12).</summary>
    public const string TestCycleForm = "run-agent-test-cycle";

    /// <summary>The name of the form that switches an agent to Live (FR-AGT-7).</summary>
    public const string SwitchToLiveForm = "switch-agent-to-live";

    /// <summary>The name of the form that switches an agent back to Demo.</summary>
    public const string SwitchToDemoForm = "switch-agent-to-demo";

    /// <summary>
    /// What switching an agent to Live is called, wherever it is: the link to the page that
    /// switches it, and the button of that page (FR-AGT-7).
    /// </summary>
    public static string SwitchToLive => "Switch the agent to Live";

    /// <summary>What switching an agent back to Demo is called, wherever it is.</summary>
    public static string SwitchToDemo => "Switch the agent to Demo";

    /// <summary>The value of the box that a user ticks to confirm the risk of trading with real money (FR-AGT-7).</summary>
    public const string RiskConfirmed = "confirmed";

    /// <summary>The title of the form that creates an agent.</summary>
    public static string CreateTitle => "Create an agent";

    /// <summary>The title of the form of the settings of an agent, with the name of the agent.</summary>
    public static string SettingsTitle(string agentName) => $"Settings of the agent {agentName}";

    /// <summary>What the button that creates an agent says.</summary>
    public static string CreateButton => "Create the agent";

    /// <summary>What the button that saves the settings of an agent says.</summary>
    public static string SaveButton => "Save the agent";

    /// <summary>
    /// The value of the button that saves a change to the capital or to a risk setting of an
    /// agent in Live, after the page asked once more (FR-UI-3).
    /// </summary>
    public const string ConfirmLive = "live";

    /// <summary>What the button that saves such a change says.</summary>
    public static string SaveForLive => "Save for Live";

    /// <summary>What the pages say about when the mode of an agent can be switched (FR-AGT-8).</summary>
    public static string ModeLocked =>
        "The mode can be switched only while the agent is a draft, or is stopped with no open position or orders.";

    /// <summary>What the form that creates an agent says of its mode: the form offers no choice of it.</summary>
    public static string NewAgentInDemo => "A new agent is in Demo. You can switch it to Live once it is created.";

    /// <summary>What a page says before something is done to an agent in Live (FR-UI-3).</summary>
    public static string ActsOnLive =>
        "This agent is in Live: this acts on your real Binance account, with real money.";

    /// <summary>
    /// What the form of the settings says when it asks once more: the capital, a risk setting,
    /// the model of a role, or whether the roles are reassigned automatically was changed for
    /// an agent in Live, and nothing is stored until its user says so again.
    /// </summary>
    public static string ConfirmSaveForLive =>
        "This agent is in Live. The capital, the risk settings, the models of the roles, and whether the roles are " +
        "reassigned automatically, as you entered them, apply to trading with real money, from the agent's next cycle. " +
        "Nothing is saved yet. Check them, and press \"" + SaveForLive + "\" to save them.";

    /// <summary>What the page that switches an agent to Live says to a form that came without the confirmation.</summary>
    public static string ConfirmRiskFirst => "Tick the box to confirm that you understand the risk. Nothing was switched.";

    /// <summary>
    /// What trading with real money risks, in plain language, on the page that switches an agent
    /// to Live (FR-AGT-7, NFR-LEG-1). The risk disclosure says it in full.
    /// </summary>
    public static IReadOnlyList<string> LiveRisks { get; } =
    [
        "In Live the agent places real orders in your Binance account, with real money. Nobody looks at an order before it is placed.",
        "Automated trading can lose all of the capital you allocate to the agent.",
        "The risk settings are deliberately aggressive, which makes large losses more likely. On Futures, leverage can cause losses quickly.",
        "Results in Demo do not predict results in Live, and past results do not predict future results. Nothing in this service is investment advice.",
    ];

    /// <summary>The value of the choice that closes the open position of an agent that is stopped (FR-AGT-11).</summary>
    public const string ClosePosition = "close";

    /// <summary>The value of the choice that keeps the open position of an agent that is stopped, with its protective orders (FR-AGT-11).</summary>
    public const string KeepPosition = "keep";

    /// <summary>What the control that closes the open position of an agent is called, wherever it is (FR-AGT-12).</summary>
    public static string ClosePositionNow => "Close position now";

    /// <summary>What that control is called in the list of the agents, where each control names its agent.</summary>
    public static string ClosePositionNowFor(string agentName) => $"{ClosePositionNow} for {agentName}";

    /// <summary>What a page says when the position of an agent could not be closed.</summary>
    public static string NotClosed =>
        "The position could not be closed: Binance refused, or gave no answer. It is open as it was, with its stop-loss and " +
        "take-profit orders. Try again, or close it at Binance.";

    /// <summary>What a page says when an agent whose position was to be closed has none.</summary>
    public static string NothingOpen => "This agent has no open position.";

    /// <summary>
    /// The value of the button that shows the symbols of the chosen market and stores nothing.
    /// The form is not interactive, so the list of symbols follows the market with a press of it.
    /// </summary>
    public const string ShowSymbols = "symbols";

    /// <summary>What that button says.</summary>
    public static string ShowSymbolsButton => "Show the symbols of this market";

    /// <summary>
    /// The value of the button that shows the models of the providers that are chosen for the
    /// roles, and stores nothing. The form is not interactive, so the list of models of a role
    /// follows its provider with a press of it.
    /// </summary>
    public const string ShowModels = "models";

    /// <summary>What that button says.</summary>
    public static string ShowModelsButton => "Show the models of the chosen providers";

    /// <summary>What the choice of a model says while none is chosen.</summary>
    public static string ChooseModel => "Choose a model";

    /// <summary>
    /// The value of the button that shows the strongest candidate of each role and stores
    /// nothing. The form is not interactive, so the candidates follow the market and the timeframe
    /// with a press of it.
    /// </summary>
    public const string ShowCandidates = "candidates";

    /// <summary>What that button says.</summary>
    public static string ShowCandidatesButton => "Show the strongest candidates";

    /// <summary>The name of the button that saves the form with the recommended team (FR-TEAM-4).</summary>
    public const string ApplyField = "Apply";

    /// <summary>The value of that button.</summary>
    public const string ApplyRecommended = "recommended";

    /// <summary>What that button says on the form of an agent that is stored.</summary>
    public static string SaveRecommendedButton => "Save with the recommended team";

    /// <summary>What that button says on the form that creates an agent.</summary>
    public static string CreateRecommendedButton => "Create with the recommended team";

    /// <summary>
    /// The name of the field that carries what the form recommended when it was shown: the
    /// button of the recommended team saves that, and nothing that its user did not see.
    /// </summary>
    public const string RecommendedField = "Recommended";

    /// <summary>
    /// What the form says when the button of the recommended team was pressed and the
    /// recommendation is not the one that the form showed: nothing is stored, and the form is
    /// back with what is recommended now.
    /// </summary>
    public static string RecommendationChanged =>
        "Nothing was saved. The recommended team is not the one that this page showed: the market or the timeframe was " +
        "changed, or the strength scores or the models of the roles have changed since. Check the candidates below, and " +
        "press the button again.";

    /// <summary>
    /// What a form recommends, as one text that the form carries: the market, the timeframe,
    /// and the recommended model of each role that has one. Two forms that recommend the same
    /// carry the same text.
    /// </summary>
    public static string RecommendedAs(RoleRecommendations recommendations, IReadOnlyList<(Role Role, ScoredModel Model)> recommended)
    {
        ArgumentNullException.ThrowIfNull(recommendations);
        ArgumentNullException.ThrowIfNull(recommended);

        return string.Join(
            ';',
            [recommendations.Market.ToString(), recommendations.Timeframe.Code(), .. recommended.Select(role => $"{role.Role}={role.Model.ModelId:N}")]);
    }

    /// <summary>The name of the box that turns the automatic reassignment of the roles on (FR-TEAM-10).</summary>
    public const string ReassignsRolesField = "ReassignsRoles";

    /// <summary>The value of that box when it is ticked.</summary>
    public const string ReassignsRolesOn = "on";

    /// <summary>What the label of that box says.</summary>
    public static string ReassignsRolesLabel => "Reassign the roles by strength automatically";

    /// <summary>
    /// What the form says about the strongest candidates, above the roles: what a candidate is,
    /// which data its score is of, and when a role is recommended one (FR-TEAM-4, FR-EXP-9).
    /// </summary>
    public static string StrengthExplained =>
        "Under each role is its strongest candidate: the model with the highest strength score in that role, on the " +
        "market and the timeframe of the agent, among the models that your own keys can use. A strength score is worked " +
        "out from what your own agents did. A model is scored from its Live data where there is enough, and from its " +
        "Demo data otherwise, and each line says which. For the Sentiment Analyst only a model that searches live is a " +
        "candidate. A candidate is recommended only when it scores above the model that the role has: a model with no " +
        "score counts as 0. You decide: choose any model for any role.";

    /// <summary>What the form says in place of the candidates while no timeframe is chosen: a score is of one market and one timeframe.</summary>
    public static string CandidatesNeedTimeframe =>
        "Choose a market and a timeframe, and press \"" + ShowCandidatesButton + "\": a strength score is of one market " +
        "and one timeframe.";

    /// <summary>What the form says under a role that no model can be recommended for (FR-EXP-4).</summary>
    public static string NotEnoughDataToRecommend => "Not enough data to recommend a model.";

    /// <summary>What the form says where the button that saves the recommended team would be, when no role is recommended another model.</summary>
    public static string NothingRecommended =>
        "No role is recommended another model now: each has its strongest candidate, has none that scores above its " +
        "model, or has not enough data.";

    /// <summary>The link from the form to the experience screen, where every strength score is.</summary>
    public static string StrengthsLink => "The strength scores of your models on this market and timeframe";

    /// <summary>
    /// What the form says under a role about its strongest candidate (FR-TEAM-4): the model,
    /// its provider, its score in the unit of the role, its sample size, and which data the
    /// score is of (FR-EXP-9), and then that the role has it, that it is recommended and against
    /// what, or why it is not. A role with no candidate says that there is not enough data
    /// (FR-EXP-4). The mode is said as a word, and never as a badge: the badge of a page is the
    /// mode of its agent.
    /// </summary>
    /// <param name="recommendations">The scored models of the market and the timeframe.</param>
    /// <param name="role">The role.</param>
    /// <param name="currentModelId">The model that the role has, or null when it has none.</param>
    public static string CandidateOf(RoleRecommendations recommendations, Role role, Guid? currentModelId)
    {
        ArgumentNullException.ThrowIfNull(recommendations);

        if (recommendations.StrongestFor(role) is not { } strongest)
        {
            return NotEnoughDataToRecommend;
        }

        var model = strongest.Slice.Model;

        var candidate =
            $"Strongest candidate: {NameOf(model.Identifier, model.DisplayName)}, of {model.ProviderName}. " +
            $"Strength score {ExperiencePages.Score(strongest.Slice)}, sample size {ExperiencePages.Samples(strongest.Slice)}, " +
            $"{NameOf(strongest.Basis)} data.";

        if (strongest.ModelId == currentModelId)
        {
            return $"{candidate} The role has it.";
        }

        var current = recommendations.Of(role, currentModelId);

        var against = (currentModelId, current) switch
        {
            (null, _) => "the role has no model yet",
            (_, null) => "the model that the role has has no strength score yet, which counts as 0",
            _ => $"the model that the role has scores {ExperiencePages.Score(current.Slice)} " +
                $"(sample size {ExperiencePages.Samples(current.Slice)}, {NameOf(current.Basis)} data)",
        };

        return recommendations.RecommendedFor(role, currentModelId) is null
            ? $"{candidate} Not recommended: it does not score above what the role has, and {against}."
            : $"{candidate} Recommended: {against}.";
    }

    /// <summary>What the form says before a warning of the team that the button of the recommended team would save (FR-TEAM-5).</summary>
    public static string OfRecommendedTeam(string warning) => $"With the recommended team: {warning}";

    /// <summary>
    /// What the form says under the box that turns the automatic reassignment on: what the
    /// service does then, how often, when not, and that it asks nobody again (FR-TEAM-10).
    /// </summary>
    /// <param name="days">How many days pass at the least between two reassignments of an agent: <c>Trading:RoleReassignmentDays</c>.</param>
    public static string ReassignsRolesExplained(int days) =>
        "When this is on, the service gives a role its recommended model on its own, by the rule above: at most once " +
        $"in {Days(days)} for the agent, only while the agent runs, and only while it has no open position. You are " +
        "told of each change with a notification, and it applies from the agent's next cycle. A model that you choose " +
        "yourself is replaced the same way while this is on. For an agent in Live this changes which models trade with " +
        "real money, and you are not asked again for each change.";

    /// <summary>When the service last gave a role of the agent another model by strength, and until when it gives none again.</summary>
    /// <param name="last">When it last did.</param>
    /// <param name="days">How many days pass at the least between two reassignments of an agent.</param>
    public static string RolesLastReassigned(DateTimeOffset last, int days, PageTimes times) =>
        $"The service last reassigned a role of this agent on {times.At(last)}, and reassigns none again before " +
        $"{times.At(last.AddDays(days))}.";

    /// <summary>What the page that switches an agent to Live says when the automatic reassignment is on for it (FR-TEAM-10, FR-UI-3).</summary>
    /// <param name="days">How many days pass at the least between two reassignments of an agent.</param>
    public static string ReassignsRolesInLive(int days) =>
        "Automatic reassignment of the roles is on for this agent: in Live the service gives its roles other models by " +
        $"strength on its own, at most once in {Days(days)}, and does not ask you again. Turn it off in the settings of " +
        "the agent first if you do not want that.";

    private static string Days(int days) =>
        days == 1 ? "1 day" : $"{days.ToString(CultureInfo.InvariantCulture)} days";

    /// <summary>What the fields of the roles are, as the form names the group of them.</summary>
    public static string RolesLegend => "Roles";

    /// <summary>What the form says about the roles, above them (FR-TEAM-1, FR-AGT-8).</summary>
    public static string RolesExplained =>
        "Every agent is run by four AI roles, which work one after the other in each cycle. Give each role a provider and " +
        "one of its models. You can choose among the models that your own key for the provider can use. A role may wait " +
        "for its model while the agent is a draft: the agent is started once every role has one. A change applies from " +
        "the agent's next cycle.";

    /// <summary>
    /// What the form says about live search, with the choices that mark the models that have it:
    /// what the mark means, which role uses it, and that it may add provider charges (FR-CYC-3).
    /// </summary>
    public static string LiveSearchExplained =>
        "A model that says \"searches live\" can look up current news and posts with the search of its provider. Only the " +
        "Sentiment Analyst searches, in every cycle of the agent. Searching may add charges of the provider to your own " +
        "key, beside the tokens of the call: for each search, and at some providers for what a search reads. The other " +
        "roles never search, whatever their model can do.";

    /// <summary>
    /// What the form says under the Sentiment Analyst when the model chosen for it cannot
    /// search live: the role still runs, and what becomes of its report (FR-CYC-3).
    /// </summary>
    public static string SentimentWithoutLiveSearch =>
        "This model cannot search live. The Sentiment Analyst still runs with it, but its report is marked \"no live data\" " +
        "and the Strategist gives it no weight. Choose a model that searches live to give the team current news and posts.";

    /// <summary>
    /// What the form says of a role whose model cannot be selected now: its user sees which
    /// model the role has, and that the agent does not run with it.
    /// </summary>
    public static string ModelNotSelectableNow =>
        "The model that this role has cannot be selected now: the key of its provider is missing or not valid, or the " +
        "model is no longer offered. The agent is not started or resumed with it. See to the key, or give the role another model.";

    /// <summary>What a page says above the roles that keep an agent from being started or resumed (FR-TEAM-1).</summary>
    public static string RolesBlock(AgentControl control) =>
        $"The agent was not {(control == AgentControl.Resume ? "resumed" : "started")}: it runs once every role of its team " +
        "has a model that your keys can use.";

    /// <summary>What a page says to a user whose email address is not verified, in place of the form that creates an agent (FR-ACC-1).</summary>
    public static string VerifyFirst =>
        "You can create an agent once your email address is verified. Open the link in the email you were sent when you registered.";

    /// <summary>What the pages say about when the market, symbol, and timeframe of an agent can be changed (FR-AGT-8).</summary>
    public static string StructureLocked =>
        "The market, the symbol, and the timeframe can be changed only while the agent is a draft, or is stopped with no open position or orders.";

    /// <summary>What the form says when one of them was changed for an agent that is neither: nothing is stored.</summary>
    public static string NotSavedStructureLocked => $"Nothing was saved. {StructureLocked}";

    /// <summary>What a page says when the exchange did not say which symbols a market has.</summary>
    public static string SymbolsUnknown =>
        "Binance gave no answer about which symbols the market has, so the symbol could not be checked and nothing was saved. Try again in a moment.";

    /// <summary>What the form says under the choice of a symbol when the exchange did not say which symbols the chosen market has.</summary>
    public static string SymbolsNotOffered(Market market) =>
        $"Binance gave no answer about which symbols {NameOf(market)} has, so none can be chosen now. Try again in a moment.";

    /// <summary>The title of the list of the agents of a user, and the caption of its table.</summary>
    public static string ListTitle => "Your agents";

    /// <summary>What the list says first: what an agent is, and where it is started, stopped, and switched.</summary>
    public static string ListExplained =>
        "An agent trades one symbol on one Binance market, on one timeframe, with the capital and the risk settings you " +
        "give it. A new agent is a draft in Demo: it trades nothing until it is started. You start, pause, resume, and " +
        "stop an agent on the page of its state, and switch it between Demo and Live there.";

    /// <summary>What the list says in place of its table to a user who has no agent.</summary>
    public static string NoAgentYet => "You have no agent yet.";

    /// <summary>The heading of the column of the list that holds the capital of an agent.</summary>
    public static string CapitalHeading => "Capital";

    /// <summary>The heading of the column of the list that holds the controls of an agent.</summary>
    public static string ActionsHeading => "Actions";

    /// <summary>
    /// The link from the list to the page of the state of an agent. A control of the list is
    /// named after its agent, so that each says what it is for by itself (NFR-USA-1).
    /// </summary>
    public static string StateOf(string agentName) => $"State of {agentName}";

    /// <summary>What the button of the list that copies an agent says.</summary>
    public static string CopyOf(string agentName) => $"Copy {agentName}";

    /// <summary>The link from the list to the page that deletes an agent.</summary>
    public static string DeleteOf(string agentName) => $"Delete {agentName}";

    /// <summary>The heading of what the states mean, under the table of the list (NFR-USA-2).</summary>
    public static string StatesHeading => "What the states mean";

    /// <summary>How many agents a user has, of how many an account can have (FR-ACC-7).</summary>
    public static string Counted(int agents, int maxAgents) =>
        $"You have {agents.ToString(CultureInfo.InvariantCulture)} of {maxAgents.ToString(CultureInfo.InvariantCulture)} agents.";

    /// <summary>The link from the list to the form that creates an agent.</summary>
    public static string CreateLink => "Create an agent";

    /// <summary>
    /// The name of the form that copies an agent. The list has one for each agent, and a page's
    /// forms each have a name of their own.
    /// </summary>
    public static string CloneForm(Guid agentId) => $"clone-agent-{agentId}";

    /// <summary>What a page says to a user who has as many agents as the fair-use limit allows (FR-ACC-7).</summary>
    public static string LimitReached(int maxAgents) =>
        $"You have {maxAgents.ToString(CultureInfo.InvariantCulture)} agents, which is the most one account can have. Delete one to create another.";

    /// <summary>A number as a page writes it and as a form takes it: digits and a decimal point, with no zeros after the last digit that counts.</summary>
    public static string Number(decimal number) => number.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>The capital of an agent, with the asset it is in: <c>1000 USDT</c>.</summary>
    public static string Capital(AgentView agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return $"{Number(agent.AllocatedCapital)} {agent.QuoteAsset}";
    }

    /// <summary>
    /// What a market is called, wherever a page writes one. A page writes no member of an enum
    /// by its name, which is a word of the code: it asks here (NFR-USA-4).
    /// </summary>
    public static string NameOf(Market market) => market switch
    {
        Market.Spot => "Spot",
        Market.Futures => "Futures",
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "The market has no name."),
    };

    /// <summary>What a mode is called, wherever a page writes one: Demo or Live.</summary>
    public static string NameOf(TradingMode mode) => mode switch
    {
        TradingMode.Demo => "Demo",
        TradingMode.Live => "Live",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode has no name."),
    };

    /// <summary>A symbol as a choice names it, with the asset that it is priced in: <c>BTCUSDT, priced in USDT</c>.</summary>
    public static string Describe(SymbolChoice symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        return $"{symbol.Symbol}, priced in {symbol.QuoteAsset}";
    }

    /// <summary>What the choice of a timeframe says while none is chosen.</summary>
    public static string ChooseTimeframe => "Choose a timeframe";

    /// <summary>A timeframe as a choice names it: its code, and what a candle of it is.</summary>
    public static string Describe(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => "15m: 15-minute candles",
        Timeframe.OneHour => "1H: 1-hour candles",
        Timeframe.FourHours => "4H: 4-hour candles",
        Timeframe.OneDay => "1D: 1-day candles",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "The timeframe has no description."),
    };

    /// <summary>What stands before the state of an agent, where a page says it in a sentence.</summary>
    public static string StateLabel => "State:";

    /// <summary>The heading of the column of a table that holds the state of an agent.</summary>
    public static string StateHeading => "State";

    /// <summary>The link to the page of the state of an agent, wherever it is.</summary>
    public static string StateLink => "State of the agent";

    /// <summary>The link to the form of the settings of an agent, wherever it is.</summary>
    public static string SettingsLink => "Settings of the agent";

    /// <summary>The link from a page of an agent to the keys of its user, where a key is what the page speaks of.</summary>
    public static string KeysLink => "Your keys";

    /// <summary>The link from a page of an agent back to the list of the agents.</summary>
    public static string BackToAgents => "Back to your agents";

    /// <summary>The state of an agent, as users read it.</summary>
    public static string Describe(AgentState state) => state switch
    {
        AgentState.Draft => "Draft",
        AgentState.Running => "Running",
        AgentState.Paused => "Paused",
        AgentState.Halted => "Halted",
        AgentState.AttentionRequired => "Attention required",
        AgentState.Stopped => "Stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state has no description."),
    };

    /// <summary>
    /// What a state means, in plain language, wherever the state is shown: whether the agent's
    /// cycles run, whether it opens a position, and what becomes of an open one (SRS 3.3,
    /// NFR-USA-2).
    /// </summary>
    public static string Explain(AgentState state) => state switch
    {
        AgentState.Draft =>
            "The agent is not started yet. It runs no cycle and trades nothing.",
        AgentState.Running =>
            "The agent's team decides once for each candle that closes, may open a position, and manages the open one.",
        AgentState.Paused =>
            "You paused the agent. It runs no cycle and opens no position. An open position stays open with its stop-loss " +
            "and take-profit orders.",
        AgentState.Halted =>
            "A loss limit was reached, and the service closes the agent's position. It runs no cycle and opens no position.",
        AgentState.AttentionRequired =>
            "Something that the agent needs is missing or failing, and it waits for you. It runs no cycle and opens no " +
            "position. An open position stays open with its stop-loss order, unless the reason says that it has none.",
        AgentState.Stopped =>
            "You stopped the agent. It runs no cycle and opens no position. An open position was closed or kept, as you chose.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state has no explanation."),
    };

    /// <summary>Why an agent came to a state, as its user reads it (FR-AGT-10).</summary>
    public static string Says(AgentStateCause cause) => cause switch
    {
        AgentStateCause.Created => "You created the agent.",
        AgentStateCause.UserStarted => "You started the agent.",
        AgentStateCause.UserPaused => "You paused the agent.",
        AgentStateCause.UserResumed => "You resumed the agent.",
        AgentStateCause.UserStopped => "You stopped the agent. It had no open position.",
        AgentStateCause.UserStoppedClosingPosition => "You stopped the agent, and chose to close its position at market.",
        AgentStateCause.UserStoppedKeepingPosition =>
            "You stopped the agent, and chose to keep its position with its stop-loss and take-profit orders.",
        AgentStateCause.ExchangeKeyDeleted =>
            "Your Binance key for the agent's mode was deleted while the agent ran. Add a key, and resume the agent.",
        AgentStateCause.ExchangeKeyInvalid =>
            "Binance refused your key for the agent's mode while the agent ran. Replace the key, or check it again on the " +
            "page of your keys, and resume the agent.",
        AgentStateCause.AiKeyDeleted =>
            "Your key for an AI provider that a role of the agent uses was deleted while the agent ran. Add a key, or give " +
            "the role a model of another provider, and resume the agent.",
        AgentStateCause.AiKeyInvalid =>
            "An AI provider refused your key, which a role of the agent uses, while the agent ran. Replace the key, or " +
            "check it again on the page of your keys, and resume the agent.",
        AgentStateCause.ModelUnavailable =>
            "The provider of a model that a role of the agent uses no longer offers it. No other model was put in its " +
            "place. Give the role another model on the page of the agent's settings, and resume the agent.",
        AgentStateCause.PositionUnprotected =>
            "The stop-loss of the agent's position could not be placed, and the position could not be closed at market " +
            "either. The position is open with no stop-loss. The service keeps trying to protect or close it. Close it " +
            "with \"Close position now\", or at Binance, and resume the agent.",
        AgentStateCause.ExchangeSettingConflict =>
            "Your Binance Futures account conflicted with how the agent trades while the agent ran: it was in hedge mode " +
            "or in Multi-Assets mode, or it held a position in the agent's symbol that the agent did not open. The " +
            "notification of the agent says which. Change it at Binance, and resume the agent.",
        AgentStateCause.DailyLossLimitReached =>
            "The agent's loss in the UTC day reached its daily loss limit, with what its open position had lost by then. " +
            "The service closes its position, and resumes the agent when the next UTC day begins.",
        AgentStateCause.DrawdownLimitReached =>
            "The agent's equity fell from its peak by its drawdown limit, with what its open position had lost by then. " +
            "The service closes its position. The agent stays halted until you resume it, and its drawdown is counted " +
            "anew from then on.",
        AgentStateCause.DailyLossHaltEnded =>
            "The UTC day in which the agent reached its daily loss limit was over, so the service resumed it.",
        AgentStateCause.AccountDisabled =>
            "An operator of the service disabled your account while the agent ran, so the agent stopped running. Now " +
            "that your account is enabled again, resume the agent.",
        AgentStateCause.CallsFailRepeatedly =>
            "The AI provider of a role of the agent kept refusing the calls with your key while the agent ran: a limit on " +
            "the requests of the key, an account at the provider that has run out, or a model that does not take the call. " +
            "The notification of the agent says which role. See to it at the provider, or give the role another model, " +
            "and resume the agent.",
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "The cause has no description."),
    };

    /// <summary>
    /// What the page of the state says about when the halt of an agent ends, for an agent that
    /// its daily loss limit halted (FR-RSK-8).
    /// </summary>
    /// <param name="ends">When the next UTC day begins: <see cref="AgentStates.HaltEnds"/>.</param>
    public static string HaltEnds(DateTimeOffset ends, PageTimes times) =>
        $"The agent is halted until {times.At(ends)}, and cannot be resumed before. From then on the service resumes it " +
        "with the first candle that closes, when your Binance key and its team allow it, and you can resume it yourself.";

    /// <summary>
    /// What a page says when an agent is not resumed, or not started again after its user
    /// stopped it, because its daily loss limit halted it for the day (FR-RSK-8).
    /// </summary>
    /// <param name="control">The control that was refused.</param>
    /// <param name="ends">When the next UTC day begins: <see cref="AgentControlResult.HaltEnds"/>.</param>
    public static string HaltedForTheDay(AgentControl control, DateTimeOffset ends, PageTimes times) => control == AgentControl.Resume
        ? $"The agent was not resumed: it reached its daily loss limit, and is halted until {times.At(ends)}. The service " +
          "resumes it then."
        : $"The agent was not started: it reached its daily loss limit in this UTC day, and does not run before {times.At(ends)}. " +
          "Start it from then on.";

    /// <summary>The name of the form of a control that starts, pauses, or resumes an agent.</summary>
    public static string FormOf(AgentControl control) => control switch
    {
        AgentControl.Start => "start-agent",
        AgentControl.Pause => "pause-agent",
        AgentControl.Resume => "resume-agent",
        AgentControl.Stop => StopForm,
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no form."),
    };

    /// <summary>What the button of a control says.</summary>
    public static string Describe(AgentControl control) => control switch
    {
        AgentControl.Start => "Start the agent",
        AgentControl.Pause => "Pause the agent",
        AgentControl.Resume => "Resume the agent",
        AgentControl.Stop => "Stop the agent",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no description."),
    };

    /// <summary>What a control does, in plain language, beside its button.</summary>
    public static string Explain(AgentControl control) => control switch
    {
        AgentControl.Start =>
            "The agent runs: its team decides once for each candle that closes, and may open a position.",
        AgentControl.Pause =>
            "The agent runs no cycle and opens no position until you resume it. An open position stays open with its " +
            "stop-loss and take-profit orders.",
        AgentControl.Resume => "The agent runs again, from the next candle that closes.",
        AgentControl.Stop =>
            "The agent runs no cycle and opens no position. You choose what happens to an open position. A stopped agent " +
            "can be started again.",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no explanation."),
    };

    /// <summary>What a page says when a control is not offered in the state that the agent is in.</summary>
    public static string NotOffered(AgentControl control, AgentState state) => control switch
    {
        AgentControl.Start => $"This agent is {InWords(state)}. Only a draft or a stopped agent is started.",
        AgentControl.Pause => $"This agent is {InWords(state)}. Only a running agent is paused.",
        AgentControl.Resume => $"This agent is {InWords(state)}. An agent is resumed when it is paused, is halted, or requires attention.",
        AgentControl.Stop => "This agent is a draft: it was never started, so there is nothing to stop.",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no refusal."),
    };

    /// <summary>What a page says when another active agent keeps an agent from starting (FR-AGT-9).</summary>
    public static string AnotherAgentActive(AgentView agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return $"You have another active agent for {agent.Symbol} on {NameOf(agent.Market)} in {NameOf(agent.Mode)}. One Binance key runs one " +
            "active agent for each market and symbol. Stop the other one first.";
    }

    /// <summary>
    /// What a page says when the account of the user at Binance keeps an agent of the Futures
    /// market from running, with what to change there (FR-BIN-F2). The service changes no
    /// setting of the whole account.
    /// </summary>
    public static string Says(AccountConflict conflict, AgentControl control, AgentView agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var (again, done) = control == AgentControl.Resume ? ("resume the agent", "resumed") : ("start the agent", "started");

        return conflict switch
        {
            AccountConflict.HedgeMode =>
                "Your Binance Futures account is in hedge mode, and an agent trades in one-way mode. The service does not " +
                $"change a setting of your whole account. Switch the position mode to one-way at Binance, and {again}.",
            AccountConflict.MultiAssetsMode =>
                "Your Binance Futures account is in Multi-Assets mode, in which Binance takes no isolated margin, and an " +
                "agent trades with isolated margin. The service does not change a setting of your whole account. Switch " +
                $"the asset mode to single-asset at Binance, and {again}.",
            AccountConflict.PositionInSymbol =>
                $"Your Binance Futures account for {NameOf(agent.Mode)} holds a position in {agent.Symbol} that this agent did not " +
                $"open. An agent trades its symbol alone. Close the position at Binance, and {again}.",
            AccountConflict.NotKnown =>
                $"Binance did not say how your Futures account is set, so the agent was not {done}. Try again in a moment.",
            _ => throw new ArgumentOutOfRangeException(nameof(conflict), conflict, "The conflict has no description."),
        };
    }

    /// <summary>A state inside a sentence: <c>a draft</c>, <c>running</c>.</summary>
    private static string InWords(AgentState state) => state switch
    {
        AgentState.Draft => "a draft",
        AgentState.AttentionRequired => "waiting for your attention",
        _ => Describe(state).ToLowerInvariant(),
    };

    /// <summary>A role of the team that runs an agent, as users read it, with its job (SRS 4.5).</summary>
    public static (string Name, string Job) Describe(Role role) => role switch
    {
        Role.MarketAnalyst => ("Market Analyst", "Reads price, volume, and indicators, and on Futures also funding and open interest."),
        Role.SentimentAnalyst => ("Sentiment Analyst", "Reads news and social context for the symbol and the wider market."),
        Role.Strategist => ("Strategist", "Combines both reports, recalled lessons, and the current position into a trade plan."),
        Role.RiskManager => ("Risk Manager", "Reviews the plan independently, and approves, reduces, or vetoes it."),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no description."),
    };

    /// <summary>The link to the page of the prompts of an agent, wherever it is.</summary>
    public static string PromptsLink => "Prompts of the agent";

    /// <summary>The title of the page of the prompts of an agent, with the name of the agent.</summary>
    public static string PromptsTitle(string agentName) => $"Prompts of the agent {agentName}";

    /// <summary>
    /// What the page of the prompts of an agent says first: what a prompt is, whose it is, and
    /// that it is read and not edited (FR-PRM-7).
    /// </summary>
    public static string PromptsExplained =>
        "A prompt is the text that tells an AI model what to do. These are the prompts that this agent uses now. " +
        "The service supplies them, they are the same for every user, and they cannot be edited.";

    /// <summary>What the page says about the version that each prompt shows (FR-PRM-6).</summary>
    public static string PromptVersionsExplained =>
        "Each prompt has a version. A version is never changed: when the service edits a prompt, the edit is the " +
        "next version, and the agent uses the latest one.";

    /// <summary>The heading of the prompts of the roles.</summary>
    public static string RolePromptsHeading => "Roles";

    /// <summary>What the page says above the prompts of the roles.</summary>
    public static string RolePromptsExplained => "What each role is told to do, and what it answers with.";

    /// <summary>The heading of the prompt of the agent's market.</summary>
    public static string MarketPromptHeading => "Market";

    /// <summary>What the page says above the prompt of the agent's market (FR-AGT-4).</summary>
    public static string MarketPromptExplained =>
        "What every role is told about the market that this agent trades on, and which actions it allows.";

    /// <summary>The heading of the prompt of the agent's timeframe.</summary>
    public static string TimeframePromptHeading => "Timeframe";

    /// <summary>What the page says above the prompt of the agent's timeframe (FR-AGT-3).</summary>
    public static string TimeframePromptExplained =>
        "What every role is told about the timeframe that this agent works on: the style of trading, the candles it " +
        "is given, and how long a position is expected to be held.";

    /// <summary>The heading of the prompt of the profile.</summary>
    public static string ProfilePromptHeading => "Profile";

    /// <summary>What the page says above the prompt of the profile (FR-PRM-5).</summary>
    public static string ProfilePromptExplained =>
        "What every role is told about how to trade. The product has one profile, and every agent trades it.";

    /// <summary>The heading of the prompt of the lesson of a closed position.</summary>
    public static string LessonPromptHeading => "Lesson";

    /// <summary>What the page says above the prompt of the lesson of a closed position (FR-EXP-6).</summary>
    public static string LessonPromptExplained =>
        "What the model of the Risk Manager is told when it writes the lesson of a closed position. It is the same " +
        "for every agent.";

    /// <summary>The name of the prompt of the lesson, as the page shows it.</summary>
    public static string LessonPromptName => "Lesson of a closed position";

    /// <summary>The version of a prompt template, as a page says it: <c>Version 1</c>.</summary>
    public static string VersionOf(PromptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return $"Version {template.Version.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>A role as the ids of its fields name it: <c>market-analyst</c>.</summary>
    public static string IdOf(Role role) => role switch
    {
        Role.MarketAnalyst => "market-analyst",
        Role.SentimentAnalyst => "sentiment-analyst",
        Role.Strategist => "strategist",
        Role.RiskManager => "risk-manager",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no id."),
    };

    /// <summary>The name of the field that brings the provider of a role: <c>MarketAnalystProvider</c>.</summary>
    public static string ProviderField(Role role) => $"{role}Provider";

    /// <summary>The name of the field that brings the model of a role: <c>MarketAnalystModel</c>.</summary>
    public static string ModelField(Role role) => $"{role}Model";

    /// <summary>The label of the choice of the provider of a role: <c>Provider of the Market Analyst</c>.</summary>
    public static string ProviderLabel(Role role) => $"Provider of the {Describe(role).Name}";

    /// <summary>The label of the choice of the model of a role: <c>Model of the Market Analyst</c>.</summary>
    public static string ModelLabel(Role role) => $"Model of the {Describe(role).Name}";

    /// <summary>
    /// A model as a choice names it: the name its provider gives it with its identifier, or the
    /// identifier alone when the provider gives no name. A model that can search live says so,
    /// which counts most for the Sentiment Analyst.
    /// </summary>
    public static string Describe(SelectableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var name = NameOf(model.Identifier, model.DisplayName);

        return model.LiveSearch ? $"{name}, searches live" : name;
    }

    /// <summary>
    /// A model as a choice names it when it cannot be selected now: the form lists it still,
    /// because the role has it.
    /// </summary>
    public static string DescribeUnselectable(SelectableModel model) => $"{Describe(model)}, cannot be selected now";

    /// <summary>A model as a page names it, from its identifier and the name its provider gives it.</summary>
    public static string NameOf(string identifier, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, identifier, StringComparison.Ordinal)
            ? identifier
            : $"{displayName} ({identifier})";

    /// <summary>
    /// What the form says under a role whose provider offers its user no model, or null when
    /// the provider offers some. A user who lacks a key for a provider is told that another
    /// provider can take the role (FR-TEAM-5).
    /// </summary>
    public static string? Says(ProviderChoice provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider switch
        {
            { Block: ModelBlock.NoKey } =>
                $"You have no key for {provider.Name}. Store one on the page of your keys, or choose another provider for this role.",
            { Block: ModelBlock.KeyNotValid } =>
                $"Your key for {provider.Name} is not valid: {provider.Name} did not take it when it was last checked, or it " +
                "was not checked yet. Replace it or check it again on the page of your keys, or choose another provider for this role.",
            { Block: ModelBlock.NotKnown } =>
                $"It is not known yet which models your key for {provider.Name} can use. Check the key again on the page of " +
                "your keys, or choose another provider for this role.",
            { Models.Count: 0 } =>
                $"Your key for {provider.Name} can use none of the models that can be selected for an agent. Choose another " +
                "provider for this role.",
            _ => null,
        };
    }

    /// <summary>What a provider is called in a sentence when the form names none that there is.</summary>
    public static string SomeProvider => "the provider";

    /// <summary>What is wrong with what was chosen for a role, at its field. It says what to do.</summary>
    /// <param name="role">The role.</param>
    /// <param name="problem">What is wrong.</param>
    /// <param name="providerName">The name of the provider that is chosen for the role.</param>
    public static string Says(Role role, RoleProblem problem, string providerName)
    {
        var name = Describe(role).Name;

        return problem switch
        {
            RoleProblem.Missing => $"Choose a model for the {name}. A role that has a model keeps one: you can give it another.",
            RoleProblem.NotOfProvider =>
                $"The model that was chosen is not one of {providerName}. Choose a model of {providerName}: the list shows them now.",
            RoleProblem.NotSelectable =>
                "That model cannot be selected: its provider no longer offers it, or it is not allowed for agents. Choose another.",
            RoleProblem.NoKey => "You have no key for the provider of that model. Store one on the page of your keys, or choose another model.",
            RoleProblem.KeyNotValid =>
                "Your key for the provider of that model is not valid. Replace it or check it again on the page of your keys, " +
                "or choose another model.",
            RoleProblem.NotKnown =>
                "It is not known yet which models your key for the provider of that model can use. Check the key again on the " +
                "page of your keys.",
            RoleProblem.NotListed => "Your key for the provider of that model cannot use it. Choose one of the models listed.",
            _ => $"Choose a model for the {name} from the list.",
        };
    }

    /// <summary>
    /// Whether what is wrong with the model of a role is about the key of its provider, so that
    /// the page of the keys is where to see to it. A role that has no model, and a model that
    /// nobody can select, are seen to in the settings of the agent.
    /// </summary>
    public static bool IsAboutAKey(RoleProblem problem) =>
        problem is RoleProblem.NoKey or RoleProblem.KeyNotValid or RoleProblem.NotKnown or RoleProblem.NotListed;

    /// <summary>What a page says of one role that keeps an agent from being started or resumed.</summary>
    public static string Says(RoleBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var name = Describe(block.Role).Name;

        return block.Problem switch
        {
            RoleProblem.NoKey =>
                $"The {name} uses {block.ModelName}, and you have no key for {block.ProviderName}. Store one, or give the role another model.",
            RoleProblem.KeyNotValid =>
                $"The {name} uses {block.ModelName}, and your key for {block.ProviderName} is not valid. Replace it or check it " +
                "again on the page of your keys, or give the role another model.",
            RoleProblem.NotKnown =>
                $"The {name} uses {block.ModelName}, and it is not known yet which models your key for {block.ProviderName} " +
                "can use. Check the key again on the page of your keys.",
            RoleProblem.NotListed =>
                $"The {name} uses {block.ModelName}, which your key for {block.ProviderName} cannot use. Give the role another model.",
            RoleProblem.NotSelectable =>
                $"The {name} uses {block.ModelName}, which cannot be selected any more: {block.ProviderName} no longer offers " +
                "it, or it is not allowed for agents. Give the role another model.",
            _ => $"The {name} has no model. Choose one on the page of the agent's settings.",
        };
    }

    /// <summary>
    /// What the form warns of when a provider holds more than one role: the team is reduced
    /// (FR-TEAM-5). Nothing keeps a user from it.
    /// </summary>
    /// <param name="providerNames">The names of the providers that hold more than one role.</param>
    public static string ReducedTeam(IReadOnlyList<string> providerNames)
    {
        ArgumentNullException.ThrowIfNull(providerNames);

        var holders = string.Join(" and ", providerNames);
        var verb = providerNames.Count == 1 ? "holds" : "each hold";

        return $"The team is reduced: {holders} {verb} more than one role. A team works best with four different providers, " +
            "which do not share the same blind spots. You can save it as it is.";
    }

    /// <summary>What the form warns of when the Strategist and the Risk Manager use the same provider (FR-TEAM-5).</summary>
    /// <param name="providerName">The name of the provider that both use.</param>
    public static string SameProviderReviews(string providerName) =>
        $"The Strategist and the Risk Manager both use {providerName}. The Risk Manager reviews the Strategist's plan " +
        "independently, and a model of the same provider is more likely to agree with it than one of another.";

    /// <summary>What the fields of the risk settings are, as the form names the group of them.</summary>
    public static string RiskLegend => "Risk settings";

    /// <summary>What the form says about the risk settings, above them (FR-RSK-1, FR-RSK-2).</summary>
    public static string RiskExplained =>
        "Every agent trades the one profile, Aggressive. These are its hard limits: the AI roles cannot override them. " +
        "They are deliberately aggressive, and make large losses more likely. A change applies from the agent's next cycle.";

    /// <summary>The bounds of a risk setting that is a percentage, and its default, as a hint under its field.</summary>
    public static string Within(Bounded<decimal> bounds) =>
        $"From {Number(bounds.Minimum)} to {Number(bounds.Maximum)} percent. The default is {Number(bounds.Default)}.";

    /// <summary>The bounds of the leverage cap, and its default, as a hint under its field.</summary>
    public static string Within(Bounded<int> bounds) =>
        $"A whole number from {Number(bounds.Minimum)} to {Number(bounds.Maximum)}. The default is {Number(bounds.Default)}.";

    /// <summary>The floor of a risk setting that is a factor, and its default, as a hint under its field.</summary>
    public static string Within(Floored<decimal> floor) =>
        $"{Number(floor.Minimum)} or higher. The default is {Number(floor.Default)}.";

    /// <summary>What the form says above the limits that no user changes (FR-RSK-2).</summary>
    public static string FixedLimitsIntro => "Two more limits are fixed. They are the same for every agent, and cannot be changed:";

    /// <summary>
    /// The limits of the profile that the specification fixes, in plain language (SRS Appendix
    /// B, NFR-USA-2). The form says them, and has no field for them.
    /// </summary>
    public static IReadOnlyList<string> FixedLimitsExplained { get; } =
    [
        $"On Spot, a position is worth at most {Number(FixedLimits.SpotPositionPercentOfEquity)} percent of the agent's equity.",
        $"An agent has no more than {Number(FixedLimits.OpenPositionsPerAgent)} position open at a time.",
    ];

    /// <summary>
    /// The stale limit, which no user changes either, in plain language (FR-CYC-9, SRS Appendix
    /// B, NFR-USA-2). It is said apart from the limits that are the same for every agent: it
    /// goes by the timeframe, and the configuration file has it.
    /// </summary>
    /// <param name="limits">The stale limit of each timeframe, in seconds.</param>
    public static string StaleLimitExplained(StaleSecondsSettings limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        return
            "One more limit goes by the timeframe, and cannot be changed either: the stale limit. A decision that the team " +
            "completes later than this after its candle closed opens no position, and can still close one or tighten its " +
            $"protection: {Stale(Timeframe.FifteenMinutes, limits.FifteenMinutes)}, {Stale(Timeframe.OneHour, limits.OneHour)}, " +
            $"{Stale(Timeframe.FourHours, limits.FourHours)}, and {Stale(Timeframe.OneDay, limits.OneDay)}.";

        static string Stale(Timeframe timeframe, int seconds) =>
            $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds on {timeframe.Code()}";
    }

    /// <summary>
    /// What a setting of an agent is called: the label of its field in the form, the heading of
    /// its column in the list of the agents, and what stands before its value where the form
    /// shows it and does not offer it for a change. The field of the symbol says its market as
    /// well: see <see cref="SymbolLabel"/>.
    /// </summary>
    public static string Label(AgentField field) => field switch
    {
        AgentField.Name => "Name",
        AgentField.Market => "Market",
        AgentField.Symbol => "Symbol",
        AgentField.Timeframe => "Timeframe",
        AgentField.AllocatedCapital => "Allocated capital",
        AgentField.RiskPerTradePercent => "Risk per trade, in percent",
        AgentField.MinRewardToRisk => "Minimum reward-to-risk ratio",
        AgentField.LeverageCap => "Leverage cap",
        AgentField.LiquidationBuffer => "Liquidation buffer",
        AgentField.DailyLossPercent => "Daily loss limit, in percent",
        AgentField.DrawdownPercent => "Drawdown limit, in percent",
        AgentField.MaxEntryDistancePercent => "Maximum entry distance, in percent",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "The setting has no label."),
    };

    /// <summary>The label of the choice of a symbol, which says the market that the symbols are of.</summary>
    public static string SymbolLabel(Market market) => $"Symbol on {NameOf(market)}";

    /// <summary>
    /// What a setting of an agent is, in plain language, as the hint under its field (NFR-USA-2).
    /// A risk setting says its bounds after it: see <see cref="Within(Bounded{decimal})"/>. The
    /// hint of the symbol names its market: see <see cref="SymbolsOf"/>.
    /// </summary>
    public static string Explain(AgentField field) => field switch
    {
        AgentField.Name => "What you call the agent. Each of your agents has a name of its own.",
        AgentField.Market =>
            "On Spot the agent buys and sells the asset itself, and only goes long. On Futures it trades USDⓈ-margined " +
            "perpetual contracts, long and short, with leverage. The market sets which actions the agent may take and " +
            "which prompt its roles are given.",
        AgentField.Timeframe =>
            "The agent decides once for each candle that closes. The timeframe sets how often its cycle runs, which " +
            "candles it reads, and which prompt its roles are given.",
        AgentField.AllocatedCapital =>
            "How much the agent may trade with, in the asset that its symbol is priced in: for BTCUSDT, an amount of USDT. " +
            "The agent's equity is this capital plus its profit or loss so far.",
        AgentField.RiskPerTradePercent =>
            "The share of the agent's equity that one trade loses when its stop-loss is hit.",
        AgentField.MinRewardToRisk =>
            "How much a trade must aim to gain for what it risks, before the agent enters it. At 1.5, a trade that loses " +
            "100 at its stop-loss must aim for a profit of 150 or more.",
        AgentField.LeverageCap =>
            "The highest leverage the agent uses. The service chooses the leverage of each order, and no model does: the " +
            "lowest that fits the position within the agent's equity, up to this cap. A position that would need more is " +
            "made smaller. It counts on Futures only: on Spot there is no leverage.",
        AgentField.LiquidationBuffer =>
            "How many times further from the entry price than the stop-loss the estimated liquidation price must be. At 2, " +
            "a trade whose stop-loss is 1 percent away is entered only when its liquidation price is 2 percent away or " +
            "more. It counts on Futures only: on Spot nothing is liquidated.",
        AgentField.DailyLossPercent =>
            "When the agent has lost this share of the equity that it had as the UTC day began, counting what its open " +
            "position has lost so far, the service closes its position and halts it. The agent runs again when the next " +
            "UTC day begins.",
        AgentField.DrawdownPercent =>
            "When the agent's equity has fallen this far below its highest, counting what its open position has lost so " +
            "far, the service closes its position and halts it. It stays halted until you resume it, and its drawdown is " +
            "counted anew from then on.",
        AgentField.MaxEntryDistancePercent =>
            "How far the price of an entry may be from the current price. A plan whose entry is further away is rejected, " +
            "and no order is placed.",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "The setting has no explanation."),
    };

    /// <summary>The hint under the choice of a symbol: whose symbols the choices are.</summary>
    public static string SymbolsOf(Market market) => $"The symbols that {NameOf(market)} has. An agent trades one symbol.";

    /// <summary>What is wrong with what was entered for a setting. It says what the setting takes.</summary>
    /// <param name="field">The setting.</param>
    /// <param name="problem">What is wrong with it.</param>
    /// <param name="bounds">The hard bounds of the risk settings, which a number outside them is told.</param>
    public static string Says(AgentField field, AgentProblem problem, AggressiveSettings bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        return (field, problem) switch
        {
            (AgentField.Name, AgentProblem.Missing) => "Enter a name.",
            (AgentField.Name, AgentProblem.TooLong) =>
                $"That name is too long. A name has {AgentRules.MaximumNameLength.ToString(CultureInfo.InvariantCulture)} characters at most.",
            (AgentField.Name, AgentProblem.NotInOneLine) => "A name is in one line. Enter it without a line break.",
            (AgentField.Name, AgentProblem.Taken) => "You have another agent of that name. Give this one a name of its own.",

            (AgentField.Market, _) => "Choose a market: Spot or Futures.",
            (AgentField.Timeframe, _) => "Choose a timeframe: 15m, 1H, 4H, or 1D.",

            (AgentField.Symbol, AgentProblem.NotOnMarket) =>
                "The chosen market does not have that symbol. Choose one of the symbols listed for the market.",
            (AgentField.Symbol, _) => "Choose a symbol.",

            (AgentField.AllocatedCapital, AgentProblem.Missing) => "Enter the capital to allocate.",
            (AgentField.AllocatedCapital, AgentProblem.TooPrecise) =>
                $"Enter the capital with {AgentRules.CapitalDecimals.ToString(CultureInfo.InvariantCulture)} decimal places at most.",
            (AgentField.AllocatedCapital, AgentProblem.OutOfBounds) =>
                $"Enter a capital above 0, and {Number(AgentRules.MaximumCapital)} at most.",
            (AgentField.AllocatedCapital, _) => "Enter the capital as a number, with digits and a decimal point: 1000 or 250.5.",

            (AgentField.LeverageCap, AgentProblem.Missing) => "Enter the leverage cap.",
            (AgentField.LeverageCap, AgentProblem.OutOfBounds) =>
                $"The leverage cap is from {Number(bounds.LeverageCap.Minimum)} to {Number(bounds.LeverageCap.Maximum)}.",
            (AgentField.LeverageCap, _) => "Enter the leverage cap as a whole number.",

            (AgentField.MinRewardToRisk, AgentProblem.Missing) => "Enter the minimum reward-to-risk ratio.",
            (AgentField.MinRewardToRisk, AgentProblem.OutOfBounds) =>
                $"The minimum reward-to-risk ratio is {FromFloor(bounds.MinRewardToRisk)}.",
            (AgentField.LiquidationBuffer, AgentProblem.Missing) => "Enter the liquidation buffer.",
            (AgentField.LiquidationBuffer, AgentProblem.OutOfBounds) =>
                $"The liquidation buffer is {FromFloor(bounds.LiquidationBuffer)}.",
            (AgentField.MinRewardToRisk or AgentField.LiquidationBuffer, AgentProblem.TooPrecise) =>
                $"Enter the number with {AgentRules.FactorDecimals.ToString(CultureInfo.InvariantCulture)} decimal places at most.",
            (AgentField.MinRewardToRisk or AgentField.LiquidationBuffer, _) =>
                "Enter a number, with digits and a decimal point: 2 or 1.5.",

            (_, AgentProblem.Missing) => "Enter a percentage.",
            (_, AgentProblem.TooPrecise) =>
                $"Enter the percentage with {AgentRules.PercentDecimals.ToString(CultureInfo.InvariantCulture)} decimal places at most.",
            (_, AgentProblem.OutOfBounds) when PercentBoundsOf(field, bounds) is { } within =>
                $"This setting is from {Number(within.Minimum)} to {Number(within.Maximum)} percent.",
            _ => "Enter the percentage as a number, with digits and a decimal point: 2 or 0.5.",
        };
    }

    /// <summary>What the list says when an agent could not be copied, or null for an outcome that says nothing.</summary>
    public static string? SaysOfCopy(SaveAgentOutcome outcome, int maxAgents) => outcome switch
    {
        SaveAgentOutcome.EmailNotVerified => VerifyFirst,
        SaveAgentOutcome.AgentLimitReached => LimitReached(maxAgents),

        // A copy is in Demo. These two are said of a copy of an agent in Live alone: the symbol
        // of an agent in Demo was held to its market there already.
        SaveAgentOutcome.SymbolsUnknown =>
            "A copy is made in Demo, and Binance gave no answer about which symbols the market has there. Nothing was copied. " +
            "Try again in a moment.",
        SaveAgentOutcome.Invalid =>
            "A copy is made in Demo, and the market of this agent does not have its symbol there. Nothing was copied.",
        _ => null,
    };

    /// <summary>What stands before the badge of the mode, under the heading of every page of an agent (FR-UI-3).</summary>
    public static string ModeLabel => "Mode:";

    /// <summary>
    /// The heading of what a page says of the mode of an agent: of the column of a table that
    /// holds the mode, and of the part of the page of the state where the mode is switched.
    /// </summary>
    public static string ModeHeading => "Mode";

    /// <summary>What a page says of the mode of an agent, beside its badge: where the agent trades.</summary>
    public static string TradesIn(TradingMode mode) => $"The agent trades in {KeyPages.Describe(mode)}.";

    /// <summary>
    /// The control that a path names, or null when it names none: <c>start</c>, <c>pause</c>, or
    /// <c>resume</c>. Stopping an agent has a page of its own.
    /// </summary>
    public static AgentControl? ControlNamed(string? text) =>
        new AgentControl?[] { AgentControl.Start, AgentControl.Pause, AgentControl.Resume }
            .SingleOrDefault(control => string.Equals(control.ToString(), text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a page says when an agent was not switched to a mode, or cannot be now. For a Live
    /// key that stands in the way, it says what is wrong with the key, and names the permission
    /// that it lacks (FR-AGT-7, FR-KEY-5).
    /// </summary>
    /// <param name="refusal">How the switch ended, or would end.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="to">The mode that was asked for.</param>
    public static string Says(ModeSwitchResult refusal, AgentView agent, TradingMode to)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(agent);

        return refusal.Outcome switch
        {
            ModeSwitchOutcome.StructureLocked => $"This agent is {InWords(agent.State)}. {ModeLocked}",
            ModeSwitchOutcome.KeyBlocks when refusal.Block is { } block => Says(block),
            ModeSwitchOutcome.SymbolsUnknown =>
                $"Binance gave no answer about which symbols {NameOf(agent.Market)} has in {NameOf(to)}, so the symbol could not be checked and " +
                "nothing was switched. Try again in a moment.",
            ModeSwitchOutcome.SymbolNotOnMarket =>
                $"{NameOf(agent.Market)} does not have {agent.Symbol} in {NameOf(to)}. Give the agent a symbol that the market has in both " +
                "Demo and Live, on the page of its settings, and switch it then.",
            ModeSwitchOutcome.RiskNotConfirmed => ConfirmRiskFirst,
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal.Outcome, "The outcome has no message."),
        };
    }

    /// <summary>What a user is told when their Binance key for Live keeps an agent from being switched to Live.</summary>
    private static string Says(StartBlock block) => block switch
    {
        StartBlock.NoKey => "No Binance key is stored for Live. Add one, and switch the agent then.",
        StartBlock.NotValid =>
            "Your Binance key for Live is not valid: Binance did not take it when it was last checked. Replace it, or check " +
            "it again on the page of your keys, and switch the agent then.",
        StartBlock.SpotTradingMissing =>
            "Your Binance key for Live lacks the Spot trading permission, which this agent's market needs. Turn on Spot " +
            "trading for the key at Binance, check the key again on the page of your keys, and switch the agent then.",
        StartBlock.FuturesTradingMissing =>
            "Your Binance key for Live lacks the Futures trading permission, which this agent's market needs. Turn on " +
            "Futures trading for the key at Binance, check the key again on the page of your keys, and switch the agent then.",
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "The block has no message."),
    };

    /// <summary>What a page says to a user who may not create one more agent, or null to one who may.</summary>
    public static string? Says(AgentCreation creation, int maxAgents) => creation switch
    {
        AgentCreation.EmailNotVerified => VerifyFirst,
        AgentCreation.AgentLimitReached => LimitReached(maxAgents),
        _ => null,
    };

    /// <summary>What a factor takes: its floor or higher, up to the highest that a factor takes at all.</summary>
    private static string FromFloor(Floored<decimal> floor) =>
        $"{Number(floor.Minimum)} or higher, and {Number(AggressiveSettings.HighestFactor)} at most";

    /// <summary>The bounds of a risk setting that is a percentage, or null for a setting that is none.</summary>
    private static Bounded<decimal>? PercentBoundsOf(AgentField field, AggressiveSettings bounds) => field switch
    {
        AgentField.RiskPerTradePercent => bounds.RiskPerTradePercent,
        AgentField.DailyLossPercent => bounds.DailyLossPercent,
        AgentField.DrawdownPercent => bounds.DrawdownPercent,
        AgentField.MaxEntryDistancePercent => bounds.MaxEntryDistancePercent,
        _ => null,
    };
}
