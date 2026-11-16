using System.Globalization;
using Ecnanib.Accounts;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.Agents;

/// <summary>
/// The agents of a user and their settings: creating, changing, copying, and deleting them
/// (FR-AGT-1, FR-AGT-2). See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// Every operation is for the user who is signed in on the request, and reaches the agents of
/// that user only. An agent of another user is not found (NFR-SEC-3).
/// </para>
/// <para>
/// What starts, pauses, resumes, and stops an agent is <see cref="IAgentControls"/>, and what
/// switches it between Demo and Live is <see cref="IAgentModes"/>: nothing here changes the
/// state of an agent or its mode.
/// </para>
/// <para>
/// The team of an agent is among its settings: each of its four roles is given a model of the
/// catalog, which says the provider too (FR-AGT-5, FR-TEAM-1). A role may be without a model
/// while its agent does not run. The agent is started only when every role has one.
/// </para>
/// </remarks>
public interface IStoredAgents
{
    /// <summary>The agents of the user, oldest first, with whether the user may create one more.</summary>
    Task<StoredAgentList> ListAsync(HttpContext request);

    /// <summary>An agent of the user, or null when the user has no agent of that id.</summary>
    Task<AgentView?> FindAsync(HttpContext request, Guid agentId);

    /// <summary>
    /// The symbols that an agent can be given on each market, in an environment: those of the
    /// configuration file that the market has there (FR-AGT-6). The exchange says which, and its
    /// answer is shared and kept for a while, so this is seldom a call.
    /// </summary>
    /// <param name="mode">The environment the agent trades in.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<IReadOnlyList<MarketSymbols>> SymbolsAsync(TradingMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// What the user can give the roles of an agent: for every AI provider, the models that can
    /// be selected at all and that the user's own key can use (FR-CAT-6, FR-CAT-9), and the
    /// provider that the configuration file gives each role by default (FR-TEAM-2, FR-TEAM-3).
    /// It asks no provider: it is answered from what was recorded of the user's keys.
    /// </summary>
    Task<RoleChoices> RoleChoicesAsync(HttpContext request);

    /// <summary>
    /// Creates an agent with what was entered, in Demo and in Draft. A user whose email address
    /// is not verified creates none, and neither does one who has as many agents as the
    /// fair-use limit allows (FR-ACC-1, FR-ACC-7). A role is given the model that was chosen for
    /// it, which is one that the user can select, and has none when none was chosen (FR-AGT-5).
    /// </summary>
    Task<SaveAgentResult> CreateAsync(HttpContext request, AgentEntry entry);

    /// <summary>
    /// Changes the settings of an agent to what was entered. The market, the symbol, and the
    /// timeframe change only while the agent is in Draft, or is Stopped with no open position
    /// or orders (FR-AGT-8). The models of its roles change in any state, and apply from its
    /// next cycle: a model that a role is newly given is one that the user can select, and a
    /// role that has a model keeps one (FR-AGT-5). A change to a risk setting is recorded in the
    /// audit trail (NFR-LOG-7).
    /// </summary>
    /// <param name="request">The request of the signed-in user.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="entry">What was entered.</param>
    /// <param name="confirmedForLive">
    /// Whether the user was asked once more, and confirmed. It counts for an agent in Live: a
    /// change to its capital, to a risk setting, or to the model of a role is not stored
    /// without it (FR-UI-3).
    /// </param>
    Task<SaveAgentResult> SaveAsync(HttpContext request, Guid agentId, AgentEntry entry, bool confirmedForLive);

    /// <summary>
    /// Creates a copy of an agent, under a name of its own, in Demo and in Draft, whatever mode
    /// the agent is in, with the models that its roles have. It counts against the fair-use
    /// limit as any agent does. An agent in Live is copied only when its market has its symbol
    /// in Demo as well (FR-AGT-6).
    /// </summary>
    Task<SaveAgentResult> CloneAsync(HttpContext request, Guid agentId);

    /// <summary>
    /// Deletes an agent, for good: one that is in Draft, or is Stopped with no open position or
    /// orders. Where there is no such agent, nothing happens.
    /// </summary>
    Task<DeleteAgentOutcome> DeleteAsync(HttpContext request, Guid agentId);
}

/// <summary>The agents of a user.</summary>
/// <param name="Agents">The agents, oldest first.</param>
/// <param name="Creation">Whether the user may create one more.</param>
/// <param name="MaxAgents">The most agents one user may have.</param>
public sealed record StoredAgentList(IReadOnlyList<AgentView> Agents, AgentCreation Creation, int MaxAgents);

/// <summary>An agent, as its user may read it.</summary>
/// <param name="Id">The id of the agent.</param>
/// <param name="Name">The name its user gave it.</param>
/// <param name="Mode">The environment it trades in.</param>
/// <param name="Market">The market it trades on.</param>
/// <param name="Symbol">The symbol it trades.</param>
/// <param name="QuoteAsset">The asset that the symbol is priced in, which the allocated capital is in.</param>
/// <param name="Timeframe">The candle length it works on.</param>
/// <param name="AllocatedCapital">The capital its user allocated to it.</param>
/// <param name="Risk">Its risk settings.</param>
/// <param name="State">Its state.</param>
/// <param name="StateCause">Why it is in that state.</param>
/// <param name="StateChangedAt">Since when it is in that state.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="StructureCanChange">
/// Whether its market, symbol, and timeframe can be changed now, and whether it can be deleted:
/// it is in Draft, or is Stopped with no open position or orders (FR-AGT-8).
/// </param>
/// <param name="HasOpenPosition">
/// Whether it has an open position now, which its user can close at any time (FR-AGT-12), and is
/// asked about when they stop the agent (FR-AGT-11).
/// </param>
/// <param name="Roles">
/// The roles of its team that have a model, each with its model, in the order of the roles
/// (FR-AGT-5). A role that is not among them has none yet.
/// </param>
public sealed record AgentView(
    Guid Id,
    string Name,
    TradingMode Mode,
    Market Market,
    string Symbol,
    string QuoteAsset,
    Timeframe Timeframe,
    decimal AllocatedCapital,
    AgentRisk Risk,
    AgentState State,
    AgentStateCause StateCause,
    DateTimeOffset StateChangedAt,
    DateTimeOffset CreatedAt,
    bool StructureCanChange,
    bool HasOpenPosition,
    IReadOnlyList<AgentRoleView> Roles);

/// <summary>
/// The risk settings of an agent that its user chooses, each within the hard bounds of the
/// configuration file (FR-RSK-2, SRS Appendix B). Percentages are of the agent's equity.
/// </summary>
/// <param name="RiskPerTradePercent">The share of equity risked on one trade.</param>
/// <param name="MinRewardToRisk">The lowest reward-to-risk ratio a plan may have at entry.</param>
/// <param name="LeverageCap">The highest leverage on Futures.</param>
/// <param name="LiquidationBuffer">How many times further than the stop-loss the liquidation price must be on Futures.</param>
/// <param name="DailyLossPercent">The loss in one day that halts the agent.</param>
/// <param name="DrawdownPercent">The fall from peak equity that halts the agent.</param>
/// <param name="MaxEntryDistancePercent">How far an entry price may be from the current price.</param>
public sealed record AgentRisk(
    decimal RiskPerTradePercent,
    decimal MinRewardToRisk,
    int LeverageCap,
    decimal LiquidationBuffer,
    decimal DailyLossPercent,
    decimal DrawdownPercent,
    decimal MaxEntryDistancePercent);

/// <summary>The symbols that an agent can be given on one market.</summary>
/// <param name="Market">The market.</param>
/// <param name="Known">
/// Whether the exchange said which symbols the market has. When it did not, no symbol is listed,
/// and no agent is given a symbol of the market until it does.
/// </param>
/// <param name="Symbols">The symbols of the configuration file that the market has, in the order of the file.</param>
public sealed record MarketSymbols(Market Market, bool Known, IReadOnlyList<SymbolChoice> Symbols);

/// <summary>A symbol that an agent can be given.</summary>
/// <param name="Symbol">The exchange's name of the symbol, such as <c>BTCUSDT</c>.</param>
/// <param name="QuoteAsset">The asset that it is priced in, such as <c>USDT</c>.</param>
public sealed record SymbolChoice(string Symbol, string QuoteAsset);

/// <summary>
/// The settings of an agent as a form brought them: text, as it was entered. What is wrong with
/// it is said field by field.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Market">The name of the market: <c>Spot</c> or <c>Futures</c>.</param>
/// <param name="Symbol">The symbol.</param>
/// <param name="Timeframe">The code of the timeframe: <c>15m</c>, <c>1H</c>, <c>4H</c>, or <c>1D</c>.</param>
/// <param name="AllocatedCapital">The allocated capital, in the quote asset of the symbol.</param>
/// <param name="RiskPerTradePercent">The risk per trade, in percent.</param>
/// <param name="MinRewardToRisk">The minimum reward-to-risk ratio.</param>
/// <param name="LeverageCap">The leverage cap, a whole number.</param>
/// <param name="LiquidationBuffer">The liquidation buffer, a factor.</param>
/// <param name="DailyLossPercent">The daily loss limit, in percent.</param>
/// <param name="DrawdownPercent">The drawdown limit, in percent.</param>
/// <param name="MaxEntryDistancePercent">The most distance of an entry from the current price, in percent.</param>
/// <param name="Roles">What was chosen for the roles of the team: for each, a provider and a model.</param>
public sealed record AgentEntry(
    string? Name,
    string? Market,
    string? Symbol,
    string? Timeframe,
    string? AllocatedCapital,
    string? RiskPerTradePercent,
    string? MinRewardToRisk,
    string? LeverageCap,
    string? LiquidationBuffer,
    string? DailyLossPercent,
    string? DrawdownPercent,
    string? MaxEntryDistancePercent,
    IReadOnlyList<RoleEntry> Roles);

/// <summary>A setting of an agent that a form has a field for.</summary>
public enum AgentField
{
    Name,
    Market,
    Symbol,
    Timeframe,
    AllocatedCapital,
    RiskPerTradePercent,
    MinRewardToRisk,
    LeverageCap,
    LiquidationBuffer,
    DailyLossPercent,
    DrawdownPercent,
    MaxEntryDistancePercent,
}

/// <summary>What is wrong with what was entered for a setting of an agent.</summary>
public enum AgentProblem
{
    /// <summary>Nothing was entered, or nothing was chosen.</summary>
    Missing,

    /// <summary>The name is longer than <see cref="AgentRules.MaximumNameLength"/> characters.</summary>
    TooLong,

    /// <summary>The name is not in one line: it holds a line break or another control character.</summary>
    NotInOneLine,

    /// <summary>The user has another agent of that name (FR-AGT-1).</summary>
    Taken,

    /// <summary>What was chosen is not among what can be chosen: no such market, timeframe, or symbol of the configuration file.</summary>
    NotOffered,

    /// <summary>The chosen market does not have the symbol (FR-AGT-6).</summary>
    NotOnMarket,

    /// <summary>What was entered is not a number, written with digits and at most one decimal point.</summary>
    NotANumber,

    /// <summary>The number has more decimal places than the setting takes.</summary>
    TooPrecise,

    /// <summary>The number is outside the bounds of the setting (FR-RSK-2).</summary>
    OutOfBounds,
}

/// <summary>How creating, changing, or copying an agent ended.</summary>
public enum SaveAgentOutcome
{
    /// <summary>The agent is stored as it was entered.</summary>
    Saved,

    /// <summary>Something that was entered is wrong. Nothing was stored.</summary>
    Invalid,

    /// <summary>The email address of the user is not verified, so they create no agent (FR-ACC-1).</summary>
    EmailNotVerified,

    /// <summary>The user has as many agents as the fair-use limit allows (FR-ACC-7).</summary>
    AgentLimitReached,

    /// <summary>
    /// The exchange did not say which symbols the market has, so the symbol could not be held to
    /// the market. Nothing was stored.
    /// </summary>
    SymbolsUnknown,

    /// <summary>
    /// The market, the symbol, or the timeframe was changed, and the agent is neither in Draft
    /// nor Stopped with no open position or orders (FR-AGT-8). Nothing was stored.
    /// </summary>
    StructureLocked,

    /// <summary>The user has no agent of that id.</summary>
    NotFound,

    /// <summary>
    /// The agent is in Live, the capital, a risk setting, or the model of a role was changed,
    /// and its user was not asked once more (FR-UI-3). Nothing was stored.
    /// </summary>
    NotConfirmedForLive,
}

/// <summary>What creating, changing, or copying an agent gave.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="AgentId">The agent that is stored now, when one is.</param>
/// <param name="Problems">What is wrong with what was entered, by field, when something is.</param>
/// <param name="RoleProblems">What is wrong with what was chosen for the roles, by role, when something is.</param>
public sealed record SaveAgentResult(
    SaveAgentOutcome Outcome,
    Guid? AgentId = null,
    IReadOnlyDictionary<AgentField, AgentProblem>? Problems = null,
    IReadOnlyDictionary<Role, RoleProblem>? RoleProblems = null)
{
    /// <summary>What is wrong with what was entered, by field. It is empty unless something is.</summary>
    public IReadOnlyDictionary<AgentField, AgentProblem> Problems { get; } =
        Problems ?? new Dictionary<AgentField, AgentProblem>();

    /// <summary>What is wrong with what was chosen for the roles, by role. It is empty unless something is.</summary>
    public IReadOnlyDictionary<Role, RoleProblem> RoleProblems { get; } =
        RoleProblems ?? new Dictionary<Role, RoleProblem>();
}

/// <summary>How deleting an agent ended.</summary>
public enum DeleteAgentOutcome
{
    /// <summary>The user has no agent of that id now: it was deleted, by this request or before it.</summary>
    Deleted,

    /// <summary>The agent is active. It is stopped before it is deleted. Nothing was deleted.</summary>
    NotStopped,

    /// <summary>The agent is stopped and has an open position or open orders. Nothing was deleted.</summary>
    HoldsPosition,
}

/// <summary>What an agent's settings must keep to, apart from the bounds of the configuration file.</summary>
public static class AgentRules
{
    /// <summary>The most characters of a name.</summary>
    public const int MaximumNameLength = 60;

    /// <summary>The most decimal places of the allocated capital.</summary>
    public const int CapitalDecimals = 8;

    /// <summary>The most decimal places of a risk setting that is a percentage.</summary>
    public const int PercentDecimals = 2;

    /// <summary>The most decimal places of a risk setting that is a factor: the reward-to-risk ratio and the liquidation buffer.</summary>
    public const int FactorDecimals = 2;

    /// <summary>The most capital that an agent can be allocated: more than any account holds, and still a number the form can take.</summary>
    public const decimal MaximumCapital = 1_000_000_000_000m;

    /// <summary>
    /// Reads what a form brought. Nothing is asked of the database or of the exchange: a name
    /// that is taken, and a symbol that its market lacks, are found by whoever stores the agent.
    /// </summary>
    /// <param name="entry">What was entered.</param>
    /// <param name="bounds">The hard bounds of the risk settings.</param>
    /// <param name="symbols">The symbols of the configuration file.</param>
    /// <param name="problems">Takes what is wrong, by field.</param>
    /// <returns>The settings, or null when something is wrong.</returns>
    internal static AgentSettings? Read(
        AgentEntry entry, AggressiveSettings bounds, IReadOnlyList<string> symbols, Dictionary<AgentField, AgentProblem> problems)
    {
        var name = (entry.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            problems[AgentField.Name] = AgentProblem.Missing;
        }
        else if (name.Any(char.IsControl))
        {
            problems[AgentField.Name] = AgentProblem.NotInOneLine;
        }
        else if (name.Length > MaximumNameLength)
        {
            problems[AgentField.Name] = AgentProblem.TooLong;
        }

        var market = Chosen(AgentField.Market, entry.Market, MarketNamed);
        var timeframe = Chosen(AgentField.Timeframe, entry.Timeframe, Timeframes.WithCode);

        var symbol = (entry.Symbol ?? string.Empty).Trim();
        if (symbol.Length == 0)
        {
            problems[AgentField.Symbol] = AgentProblem.Missing;
        }
        else if (!symbols.Contains(symbol, StringComparer.Ordinal))
        {
            problems[AgentField.Symbol] = AgentProblem.NotOffered;
        }

        var capital = Number(AgentField.AllocatedCapital, entry.AllocatedCapital, CapitalDecimals);
        if (capital is <= 0 or > MaximumCapital)
        {
            problems[AgentField.AllocatedCapital] = AgentProblem.OutOfBounds;
        }

        var riskPerTrade = Percent(AgentField.RiskPerTradePercent, entry.RiskPerTradePercent, bounds.RiskPerTradePercent);
        var rewardToRisk = Factor(AgentField.MinRewardToRisk, entry.MinRewardToRisk, bounds.MinRewardToRisk);
        var leverage = Whole(AgentField.LeverageCap, entry.LeverageCap, bounds.LeverageCap);
        var liquidationBuffer = Factor(AgentField.LiquidationBuffer, entry.LiquidationBuffer, bounds.LiquidationBuffer);
        var dailyLoss = Percent(AgentField.DailyLossPercent, entry.DailyLossPercent, bounds.DailyLossPercent);
        var drawdown = Percent(AgentField.DrawdownPercent, entry.DrawdownPercent, bounds.DrawdownPercent);
        var entryDistance = Percent(AgentField.MaxEntryDistancePercent, entry.MaxEntryDistancePercent, bounds.MaxEntryDistancePercent);

        return problems.Count > 0
            ? null
            : new AgentSettings(
                name,
                market!.Value,
                symbol,
                timeframe!.Value,
                capital!.Value,
                new AgentRisk(
                    riskPerTrade!.Value,
                    rewardToRisk!.Value,
                    leverage!.Value,
                    liquidationBuffer!.Value,
                    dailyLoss!.Value,
                    drawdown!.Value,
                    entryDistance!.Value));

        TChoice? Chosen<TChoice>(AgentField field, string? text, Func<string?, TChoice?> among)
            where TChoice : struct
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                problems[field] = AgentProblem.Missing;

                return null;
            }

            if (among(text.Trim()) is not { } choice)
            {
                problems[field] = AgentProblem.NotOffered;

                return null;
            }

            return choice;
        }

        decimal? Number(AgentField field, string? text, int decimals)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                problems[field] = AgentProblem.Missing;

                return null;
            }

            // Digits, and at most one decimal point: the same in every country, with no sign,
            // no space in it, and no letter.
            if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                problems[field] = AgentProblem.NotANumber;

                return null;
            }

            if (decimal.Round(number, decimals) != number)
            {
                problems[field] = AgentProblem.TooPrecise;

                return null;
            }

            return number;
        }

        decimal? Percent(AgentField field, string? text, Bounded<decimal> within)
        {
            if (Number(field, text, PercentDecimals) is not { } number)
            {
                return null;
            }

            if (number < within.Minimum || number > within.Maximum)
            {
                problems[field] = AgentProblem.OutOfBounds;

                return null;
            }

            return number;
        }

        // A factor has a floor, and no highest value of its own: it is held to the highest that
        // a factor takes at all, which the configuration file is held to as well.
        decimal? Factor(AgentField field, string? text, Floored<decimal> within)
        {
            if (Number(field, text, FactorDecimals) is not { } number)
            {
                return null;
            }

            if (number < within.Minimum || number > AggressiveSettings.HighestFactor)
            {
                problems[field] = AgentProblem.OutOfBounds;

                return null;
            }

            return number;
        }

        int? Whole(AgentField field, string? text, Bounded<int> within)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                problems[field] = AgentProblem.Missing;

                return null;
            }

            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                problems[field] = AgentProblem.NotANumber;

                return null;
            }

            if (number < within.Minimum || number > within.Maximum)
            {
                problems[field] = AgentProblem.OutOfBounds;

                return null;
            }

            return number;
        }
    }

    /// <summary>The market that a name names, or null when it names none: <c>Spot</c> or <c>Futures</c>.</summary>
    public static Market? MarketNamed(string? name) =>
        Enum.GetValues<Market>()
            .Cast<Market?>()
            .SingleOrDefault(market => string.Equals(market.ToString(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name of a copy of an agent: "Copy of" and the name of the agent, with a number after
    /// it when the user has an agent of that name already, and cut to the length of a name.
    /// </summary>
    /// <param name="name">The name of the agent that is copied.</param>
    /// <param name="taken">The names of the agents that the user has.</param>
    internal static string CopyNameOf(string name, IReadOnlyCollection<string> taken)
    {
        var copy = Cut($"Copy of {name}", MaximumNameLength);

        for (var number = 2; taken.Contains(copy, StringComparer.Ordinal); number++)
        {
            var suffix = $" ({number.ToString(CultureInfo.InvariantCulture)})";
            copy = Cut($"Copy of {name}", MaximumNameLength - suffix.Length).TrimEnd() + suffix;
        }

        return copy;

        static string Cut(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd();
    }
}

/// <summary>The settings of an agent that a form brought, read and found to be in order.</summary>
internal sealed record AgentSettings(
    string Name, Market Market, string Symbol, Timeframe Timeframe, decimal AllocatedCapital, AgentRisk Risk);
