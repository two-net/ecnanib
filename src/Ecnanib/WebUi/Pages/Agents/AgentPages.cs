using System.Globalization;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.Notifications;
using Ecnanib.Platform.Settings;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Account;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// What the pages of a user's agents have in common: the names of their forms, how a setting is
/// written, what they say about a setting that cannot be stored, and what they say about the
/// state of an agent and the controls that change it (FR-AGT-1, FR-AGT-2, FR-AGT-10), and what
/// they say about its mode and about switching it (FR-AGT-7, FR-UI-3), and what they say about
/// the prompts that it uses (FR-PRM-7). Every setting and every state is explained in plain
/// language (NFR-USA-2).
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

    /// <summary>The name of the form that switches an agent to Live (FR-AGT-7).</summary>
    public const string SwitchToLiveForm = "switch-agent-to-live";

    /// <summary>The name of the form that switches an agent back to Demo.</summary>
    public const string SwitchToDemoForm = "switch-agent-to-demo";

    /// <summary>The value of the box that a user ticks to confirm the risk of trading with real money (FR-AGT-7).</summary>
    public const string RiskConfirmed = "confirmed";

    /// <summary>
    /// The value of the button that saves a change to the capital or to a risk setting of an
    /// agent in Live, after the page asked once more (FR-UI-3).
    /// </summary>
    public const string ConfirmLive = "live";

    /// <summary>What the button that saves such a change says.</summary>
    public const string SaveForLive = "Save for Live";

    /// <summary>What the pages say about when the mode of an agent can be switched (FR-AGT-8).</summary>
    public const string ModeLocked =
        "The mode can be switched only while the agent is a draft, or is stopped with no open position or orders.";

    /// <summary>What a page says before something is done to an agent in Live (FR-UI-3).</summary>
    public const string ActsOnLive =
        "This agent is in Live: this acts on your real Binance account, with real money.";

    /// <summary>
    /// What the form of the settings says when it asks once more: the capital, a risk setting,
    /// or the model of a role of an agent in Live was changed, and nothing is stored until its
    /// user says so again.
    /// </summary>
    public const string ConfirmSaveForLive =
        "This agent is in Live. The capital, the risk settings, and the models of the roles you entered apply to trading " +
        "with real money, from the agent's next cycle. Nothing is saved yet. Check them, and press \"" + SaveForLive +
        "\" to save them.";

    /// <summary>What the page that switches an agent to Live says to a form that came without the confirmation.</summary>
    public const string ConfirmRiskFirst = "Tick the box to confirm that you understand the risk. Nothing was switched.";

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
    public const string ClosePositionNow = "Close position now";

    /// <summary>What a page says when the position of an agent could not be closed.</summary>
    public const string NotClosed =
        "The position could not be closed: Binance refused, or gave no answer. It is open as it was, with its stop-loss and " +
        "take-profit orders. Try again, or close it at Binance.";

    /// <summary>What a page says when an agent whose position was to be closed has none.</summary>
    public const string NothingOpen = "This agent has no open position.";

    /// <summary>
    /// The value of the button that shows the symbols of the chosen market and stores nothing.
    /// A page is not interactive, so the list of symbols follows the market with a press of it.
    /// </summary>
    public const string ShowSymbols = "symbols";

    /// <summary>
    /// The value of the button that shows the models of the providers that are chosen for the
    /// roles, and stores nothing. A page is not interactive, so the list of models of a role
    /// follows its provider with a press of it.
    /// </summary>
    public const string ShowModels = "models";

    /// <summary>What that button says.</summary>
    public const string ShowModelsButton = "Show the models of the chosen providers";

    /// <summary>What the choice of a model says while none is chosen.</summary>
    public const string ChooseModel = "Choose a model";

    /// <summary>What the form says about the roles, above them (FR-TEAM-1, FR-AGT-8).</summary>
    public const string RolesExplained =
        "Every agent is run by four AI roles, which work one after the other in each cycle. Give each role a provider and " +
        "one of its models. You can choose among the models that your own key for the provider can use. A role may wait " +
        "for its model while the agent is a draft: the agent is started once every role has one. A change applies from " +
        "the agent's next cycle.";

    /// <summary>
    /// What the form says about live search, with the choices that mark the models that have it:
    /// what the mark means, which role uses it, and that it may add provider charges (FR-CYC-3).
    /// </summary>
    public const string LiveSearchExplained =
        "A model that says \"searches live\" can look up current news and posts with the search of its provider. Only the " +
        "Sentiment Analyst searches, in every cycle of the agent. Searching may add charges of the provider to your own " +
        "key, beside the tokens of the call: for each search, and at some providers for what a search reads. The other " +
        "roles never search, whatever their model can do.";

    /// <summary>
    /// What the form says under the Sentiment Analyst when the model chosen for it cannot
    /// search live: the role still runs, and what becomes of its report (FR-CYC-3).
    /// </summary>
    public const string SentimentWithoutLiveSearch =
        "This model cannot search live. The Sentiment Analyst still runs with it, but its report is marked \"no live data\" " +
        "and the Strategist gives it no weight. Choose a model that searches live to give the team current news and posts.";

    /// <summary>
    /// What the form says of a role whose model cannot be selected now: its user sees which
    /// model the role has, and that the agent does not run with it.
    /// </summary>
    public const string ModelNotSelectableNow =
        "The model that this role has cannot be selected now: the key of its provider is missing or not valid, or the " +
        "model is no longer offered. The agent is not started or resumed with it. See to the key, or give the role another model.";

    /// <summary>What a page says above the roles that keep an agent from being started or resumed (FR-TEAM-1).</summary>
    public static string RolesBlock(AgentControl control) =>
        $"The agent was not {(control == AgentControl.Resume ? "resumed" : "started")}: it runs once every role of its team " +
        "has a model that your keys can use.";

    /// <summary>What a page says to a user whose email address is not verified, in place of the form that creates an agent (FR-ACC-1).</summary>
    public const string VerifyFirst =
        "You can create an agent once your email address is verified. Open the link in the email you were sent when you registered.";

    /// <summary>What the pages say about when the market, symbol, and timeframe of an agent can be changed (FR-AGT-8).</summary>
    public const string StructureLocked =
        "The market, the symbol, and the timeframe can be changed only while the agent is a draft, or is stopped with no open position or orders.";

    /// <summary>What a page says when the exchange did not say which symbols a market has.</summary>
    public const string SymbolsUnknown =
        "Binance gave no answer about which symbols the market has, so the symbol could not be checked and nothing was saved. Try again in a moment.";

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

    /// <summary>A timeframe as a choice names it: its code, and what a candle of it is.</summary>
    public static string Describe(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => "15m: 15-minute candles",
        Timeframe.OneHour => "1H: 1-hour candles",
        Timeframe.FourHours => "4H: 4-hour candles",
        Timeframe.OneDay => "1D: 1-day candles",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "The timeframe has no description."),
    };

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
            "A loss limit was reached, and the agent's position was closed. It runs no cycle and opens no position.",
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
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "The cause has no description."),
    };

    /// <summary>What a notification is about, as its user reads it (FR-NTF-1).</summary>
    public static string Describe(NotificationType type) => type switch
    {
        NotificationType.ProtectionFailure => "Order or protection failure",
        NotificationType.EntrySkipped => "Entry skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The type has no description."),
    };

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

        return $"You have another active agent for {agent.Symbol} on {agent.Market} in {agent.Mode}. One Binance key runs one " +
            "active agent for each market and symbol. Stop the other one first.";
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
    public const string PromptsLink = "Prompts of the agent";

    /// <summary>
    /// What the page of the prompts of an agent says first: what a prompt is, whose it is, and
    /// that it is read and not edited (FR-PRM-7).
    /// </summary>
    public const string PromptsExplained =
        "A prompt is the text that tells an AI model what to do. These are the prompts that this agent uses now. " +
        "The service supplies them, they are the same for every user, and they cannot be edited.";

    /// <summary>What the page says about the version that each prompt shows (FR-PRM-6).</summary>
    public const string PromptVersionsExplained =
        "Each prompt has a version. A version is never changed: when the service edits a prompt, the edit is the " +
        "next version, and the agent uses the latest one.";

    /// <summary>What the page says above the prompts of the roles.</summary>
    public const string RolePromptsExplained = "What each role is told to do, and what it answers with.";

    /// <summary>What the page says above the prompt of the agent's market (FR-AGT-4).</summary>
    public const string MarketPromptExplained =
        "What every role is told about the market that this agent trades on, and which actions it allows.";

    /// <summary>What the page says above the prompt of the agent's timeframe (FR-AGT-3).</summary>
    public const string TimeframePromptExplained =
        "What every role is told about the timeframe that this agent works on: the style of trading, the candles it " +
        "is given, and how long a position is expected to be held.";

    /// <summary>What the page says above the prompt of the profile (FR-PRM-5).</summary>
    public const string ProfilePromptExplained =
        "What every role is told about how to trade. The product has one profile, and every agent trades it.";

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
    public const string FixedLimitsIntro = "Two more limits are fixed. They are the same for every agent, and cannot be changed:";

    /// <summary>
    /// The limits of the profile that the specification fixes, in plain language (SRS Appendix
    /// B, NFR-USA-2). The form says them, and has no field for them.
    /// </summary>
    public static IReadOnlyList<string> FixedLimitsExplained { get; } =
    [
        $"On Spot, a position is worth at most {Number(FixedLimits.SpotPositionPercentOfEquity)} percent of the agent's equity.",
        $"An agent has no more than {Number(FixedLimits.OpenPositionsPerAgent)} position open at a time.",
    ];

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
                $"Binance gave no answer about which symbols {agent.Market} has in {to}, so the symbol could not be checked and " +
                "nothing was switched. Try again in a moment.",
            ModeSwitchOutcome.SymbolNotOnMarket =>
                $"{agent.Market} does not have {agent.Symbol} in {to}. Give the agent a symbol that the market has in both " +
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
