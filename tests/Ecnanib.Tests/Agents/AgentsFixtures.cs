using System.Collections.Concurrent;
using System.Net;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Agents;

/// <summary>A row of the <c>agents</c> table, read by a test directly.</summary>
internal sealed record StoredAgent(
    Guid Id,
    Guid UserId,
    string Name,
    string Mode,
    string Market,
    string Symbol,
    string QuoteAsset,
    string Timeframe,
    decimal AllocatedCapital,
    decimal RiskPerTradePercent,
    decimal MinRewardToRisk,
    int LeverageCap,
    decimal LiquidationBuffer,
    decimal DailyLossPercent,
    decimal DrawdownPercent,
    decimal MaxEntryDistancePercent,
    string State,
    string StateCause,
    DateTimeOffset StateChangedAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>Every agent, oldest first.</summary>
    public static IReadOnlyList<StoredAgent> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, user_id, name, mode, market, symbol, quote_asset, timeframe, allocated_capital,
                   risk_per_trade_percent, min_reward_to_risk, leverage_cap, liquidation_buffer, daily_loss_percent,
                   drawdown_percent, max_entry_distance_percent, state, state_cause, state_changed_at, created_at
            FROM agents ORDER BY created_at, id
            """,
            connection);
        using var reader = command.ExecuteReader();

        var agents = new List<StoredAgent>();
        while (reader.Read())
        {
            agents.Add(new StoredAgent(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetInt32(11),
                reader.GetDecimal(12),
                reader.GetDecimal(13),
                reader.GetDecimal(14),
                reader.GetDecimal(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetFieldValue<DateTimeOffset>(18),
                reader.GetFieldValue<DateTimeOffset>(19)));
        }

        return agents;
    }

    /// <summary>The agents of a user, oldest first.</summary>
    public static IReadOnlyList<StoredAgent> Of(TestDatabase database, Guid userId) =>
        [.. All(database).Where(agent => agent.UserId == userId)];

    /// <summary>The agent of a name, which must be there, and the only one of that name.</summary>
    public static StoredAgent Named(TestDatabase database, string name) =>
        All(database).Single(agent => agent.Name == name);
}

/// <summary>A row of the <c>agent_state_changes</c> table, read by a test directly.</summary>
internal sealed record StoredAgentStateChange(
    Guid Id, Guid AgentId, Guid UserId, string Mode, string State, string Cause, DateTimeOffset ChangedAt)
{
    /// <summary>Every change, in the order they were recorded in.</summary>
    public static IReadOnlyList<StoredAgentStateChange> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT id, agent_id, user_id, mode, state, cause, changed_at FROM agent_state_changes ORDER BY changed_at, id",
            connection);
        using var reader = command.ExecuteReader();

        var changes = new List<StoredAgentStateChange>();
        while (reader.Read())
        {
            changes.Add(new StoredAgentStateChange(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return changes;
    }

    /// <summary>The changes of the state of one agent, in the order they were recorded in.</summary>
    public static IReadOnlyList<StoredAgentStateChange> Of(TestDatabase database, Guid agentId) =>
        [.. All(database).Where(change => change.AgentId == agentId)];

    /// <summary>The changes of one agent as a test names them: each as <c>State: Cause</c>, in order.</summary>
    public static IReadOnlyList<string> Told(TestDatabase database, Guid agentId) =>
        [.. Of(database, agentId).Select(change => $"{change.State}: {change.Cause}")];
}

/// <summary>
/// A row of the <c>agent_roles</c> table, read by a test directly, with the provider and the
/// identifier of the model that it points at.
/// </summary>
internal sealed record StoredAgentRole(Guid AgentId, Guid UserId, string Role, Guid ModelId, string Provider, string Model)
{
    /// <summary>Every role that has a model, by agent, in the order of the roles.</summary>
    public static IReadOnlyList<StoredAgentRole> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT role.agent_id, role.user_id, role.role, role.model_id, model.provider, model.identifier
            FROM agent_roles AS role
            JOIN ai_models AS model ON model.id = role.model_id
            ORDER BY role.agent_id
            """,
            connection);
        using var reader = command.ExecuteReader();

        var roles = new List<StoredAgentRole>();
        while (reader.Read())
        {
            roles.Add(new StoredAgentRole(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5)));
        }

        // The roles in the order they work in, which is the order of the enum, and not of their names.
        return [.. roles.OrderBy(role => role.AgentId).ThenBy(role => Enum.Parse<Role>(role.Role))];
    }

    /// <summary>The roles of one agent that have a model, in the order of the roles.</summary>
    public static IReadOnlyList<StoredAgentRole> Of(TestDatabase database, Guid agentId) =>
        [.. All(database).Where(role => role.AgentId == agentId)];

    /// <summary>The team of one agent as a test names it: each role as <c>Role: identifier</c>, in order.</summary>
    public static IReadOnlyList<string> Told(TestDatabase database, Guid agentId) =>
        [.. Of(database, agentId).Select(role => $"{role.Role}: {role.Model}")];
}

/// <summary>What a browser does with the pages of a user's agents.</summary>
internal static class AgentBrowser
{
    /// <summary>The page that lists the agents of the signed-in user.</summary>
    public const string AgentsPage = "/agents";

    /// <summary>The page that creates an agent.</summary>
    public const string NewAgentPage = "/agents/new";

    /// <summary>The form that creates an agent, or changes one.</summary>
    public const string SaveForm = "save-agent";

    /// <summary>The form that deletes one.</summary>
    public const string DeleteForm = "delete-agent";

    /// <summary>The page that changes the settings of an agent.</summary>
    public static string AgentPage(Guid agentId) => $"/agents/{agentId}";

    /// <summary>The page that deletes it.</summary>
    public static string DeleteAgentPage(Guid agentId) => $"/agents/{agentId}/delete";

    /// <summary>The form of the list that copies an agent.</summary>
    public static string CloneForm(Guid agentId) => $"clone-agent-{agentId}";

    /// <summary>The form that starts an agent.</summary>
    public const string StartForm = "start-agent";

    /// <summary>The form that pauses one.</summary>
    public const string PauseForm = "pause-agent";

    /// <summary>The form that resumes one.</summary>
    public const string ResumeForm = "resume-agent";

    /// <summary>The form that stops one.</summary>
    public const string StopForm = "stop-agent";

    /// <summary>The form that closes the open position of one.</summary>
    public const string ClosePositionForm = "close-agent-position";

    /// <summary>The page of the state of an agent, with the forms that start, pause, and resume it.</summary>
    public static string AgentStatePage(Guid agentId) => $"/agents/{agentId}/state";

    /// <summary>The page of the prompts that an agent uses.</summary>
    public static string AgentPromptsPage(Guid agentId) => $"/agents/{agentId}/prompts";

    /// <summary>The page that stops it.</summary>
    public static string StopAgentPage(Guid agentId) => $"/agents/{agentId}/stop";

    /// <summary>The page that closes its open position.</summary>
    public static string ClosePositionPage(Guid agentId) => $"/agents/{agentId}/close-position";

    /// <summary>The page that switches an agent to Live.</summary>
    public static string SwitchToLivePage(Guid agentId) => $"/agents/{agentId}/live";

    /// <summary>The page that switches it back to Demo.</summary>
    public static string SwitchToDemoPage(Guid agentId) => $"/agents/{agentId}/demo";

    /// <summary>
    /// The page that asks once more before an agent is started, paused, or resumed:
    /// <c>start</c>, <c>pause</c>, or <c>resume</c>.
    /// </summary>
    public static string ConfirmControlPage(Guid agentId, string control) => $"/agents/{agentId}/confirm/{control}";

    /// <summary>The form that switches an agent to Live.</summary>
    public const string SwitchToLiveForm = "switch-agent-to-live";

    /// <summary>The form that switches it back to Demo.</summary>
    public const string SwitchToDemoForm = "switch-agent-to-demo";

    /// <summary>What the form that switches an agent to Live brings when the box that confirms the risk is ticked.</summary>
    public static readonly (string Name, string Value) RiskConfirmed = ("Risk", "confirmed");

    /// <summary>The badge of a mode, as a page writes it.</summary>
    public static string Badge(TradingMode mode) =>
        $"<span class=\"mode-badge mode-{mode.ToString().ToLowerInvariant()}\">{mode}</span>";

    /// <summary>
    /// What the form brings for an agent whose settings are all in order: within the bounds of
    /// the file of the tests, on a market that has the symbol.
    /// </summary>
    public static (string Name, string Value)[] Entry(
        string name = "Swing",
        string market = "Spot",
        string symbol = "BTCUSDT",
        string timeframe = "1H",
        string capital = "1000",
        string riskPerTrade = "1.5",
        string minRewardToRisk = "1.75",
        string leverageCap = "4",
        string liquidationBuffer = "2.5",
        string dailyLoss = "5",
        string drawdown = "20",
        string entryDistance = "0.8") =>
    [
        ("Name", name),
        ("Market", market),
        ("Symbol", symbol),
        ("Timeframe", timeframe),
        ("AllocatedCapital", capital),
        ("RiskPerTradePercent", riskPerTrade),
        ("MinRewardToRisk", minRewardToRisk),
        ("LeverageCap", leverageCap),
        ("LiquidationBuffer", liquidationBuffer),
        ("DailyLossPercent", dailyLoss),
        ("DrawdownPercent", drawdown),
        ("MaxEntryDistancePercent", entryDistance),
    ];

    /// <summary>What a form brings, with one field as something else.</summary>
    public static (string Name, string Value)[] With(this (string Name, string Value)[] fields, string name, string value) =>
        [.. fields.Select(field => field.Name == name ? (name, value) : field)];

    /// <summary>The four roles, as the fields of the form and the rows of the table name them, in the order they work in.</summary>
    public static readonly IReadOnlyList<string> Roles = ["MarketAnalyst", "SentimentAnalyst", "Strategist", "RiskManager"];

    /// <summary>
    /// What a form brings, with a provider and a model chosen for one role: <c>MarketAnalyst</c>,
    /// <c>claude</c>, and the id of the model, or nothing where no model is chosen.
    /// </summary>
    public static (string Name, string Value)[] WithRole(
        this (string Name, string Value)[] fields, string role, string provider, Guid? model) =>
        [
            .. fields.Where(field => field.Name != $"{role}Provider" && field.Name != $"{role}Model"),
            ($"{role}Provider", provider),
            ($"{role}Model", model?.ToString() ?? string.Empty),
        ];

    /// <summary>What a form brings, with the same provider and model chosen for every role.</summary>
    public static (string Name, string Value)[] WithTeam(this (string Name, string Value)[] fields, string provider, Guid model) =>
        Roles.Aggregate(fields, (brought, role) => brought.WithRole(role, provider, model));

    /// <summary>The choice of a page with an id, whole.</summary>
    private static string ChoiceOn(string page, string id)
    {
        var choice = System.Text.RegularExpressions.Regex.Match(
            page, $"<select id=\"{id}\".*?</select>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(choice.Success, $"The page has no choice with the id {id}:{Environment.NewLine}{page}");

        return choice.Value;
    }

    /// <summary>What a choice of a page offers, in order: each as its value and what it says.</summary>
    private static IReadOnlyList<(string Value, string Text, bool Selected)> OptionsOn(string page, string id) =>
    [
        .. System.Text.RegularExpressions.Regex.Matches(ChoiceOn(page, id), "<option value(?:=\"([^\"]*)\")?( selected)?>([^<]*)</option>")
            .Select(option => (option.Groups[1].Value, WebUtility.HtmlDecode(option.Groups[3].Value), option.Groups[2].Success)),
    ];

    /// <summary>The code of the provider that the form on a page has chosen for a role: <c>market-analyst</c>.</summary>
    public static string ProviderChosenOn(string page, string role) =>
        OptionsOn(page, $"agent-{role}-provider").Single(option => option.Selected).Value;

    /// <summary>The models that the form on a page offers for a role, in order, as it names them. The first choice, of no model, is not among them.</summary>
    public static IReadOnlyList<string> ModelsOfferedOn(string page, string role) =>
        [.. OptionsOn(page, $"agent-{role}-model").Where(option => option.Value.Length > 0).Select(option => option.Text)];

    /// <summary>What the form on a page has chosen as the model of a role, as it names it: <c>Choose a model</c> when none is.</summary>
    public static string ModelChosenOn(string page, string role) =>
        OptionsOn(page, $"agent-{role}-model").Single(option => option.Selected).Text;

    /// <summary>What a page warns of, in order.</summary>
    public static IReadOnlyList<string> WarningsOn(string page) =>
    [
        .. System.Text.RegularExpressions.Regex.Matches(page, "<p class=\"form-warning\" role=\"note\">([^<]*)</p>")
            .Select(warning => WebUtility.HtmlDecode(warning.Groups[1].Value)),
    ];

    /// <summary>Fills in the form that creates an agent and saves it, and follows where the answer leads.</summary>
    public static Task<(string Path, string Page)> CreateAgentAsync(this HttpClient client, params (string Name, string Value)[] fields) =>
        client.SubmitAsync(NewAgentPage, SaveForm, fields);

    /// <summary>Fills in the form that changes an agent and saves it, in the same way.</summary>
    public static Task<(string Path, string Page)> SaveAgentAsync(
        this HttpClient client, Guid agentId, params (string Name, string Value)[] fields) =>
        client.SubmitAsync(AgentPage(agentId), SaveForm, fields);

    /// <summary>Presses the button of the list that copies an agent.</summary>
    public static Task<(string Path, string Page)> CloneAgentAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(AgentsPage, CloneForm(agentId));

    /// <summary>Presses the button of the page that deletes an agent.</summary>
    public static Task<(string Path, string Page)> DeleteAgentAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(DeleteAgentPage(agentId), DeleteForm);

    /// <summary>Presses the button of the page of an agent's state that starts it, and follows where the answer leads.</summary>
    public static Task<(string Path, string Page)> StartAgentAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(AgentStatePage(agentId), StartForm);

    /// <summary>Presses the button that pauses it, in the same way.</summary>
    public static Task<(string Path, string Page)> PauseAgentAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(AgentStatePage(agentId), PauseForm);

    /// <summary>Presses the button that resumes it, in the same way.</summary>
    public static Task<(string Path, string Page)> ResumeAgentAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(AgentStatePage(agentId), ResumeForm);

    /// <summary>
    /// Presses the button of the page that stops an agent, with what was chosen for its open
    /// position when something was: <c>close</c> or <c>keep</c>.
    /// </summary>
    public static Task<(string Path, string Page)> StopAgentAsync(this HttpClient client, Guid agentId, string? position = null) =>
        position is null
            ? client.SubmitAsync(StopAgentPage(agentId), StopForm)
            : client.SubmitAsync(StopAgentPage(agentId), StopForm, ("Position", position));

    /// <summary>Presses the button of the page that closes the open position of an agent.</summary>
    public static Task<(string Path, string Page)> ClosePositionAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(ClosePositionPage(agentId), ClosePositionForm);

    /// <summary>
    /// Ticks the box that confirms the risk on the page that switches an agent to Live, presses
    /// its button, and follows where the answer leads. The page is shown to a user who has just
    /// proved who they are, and whose agent nothing keeps in Demo.
    /// </summary>
    public static Task<(string Path, string Page)> SwitchToLiveAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(SwitchToLivePage(agentId), SwitchToLiveForm, RiskConfirmed);

    /// <summary>Presses the button of the page that switches an agent back to Demo.</summary>
    public static Task<(string Path, string Page)> SwitchToDemoAsync(this HttpClient client, Guid agentId) =>
        client.SubmitAsync(SwitchToDemoPage(agentId), SwitchToDemoForm);

    /// <summary>
    /// Presses the button of the page that asks once more before an agent is started, paused, or
    /// resumed, as the user of an agent in Live does: <c>start</c>, <c>pause</c>, or <c>resume</c>.
    /// </summary>
    public static Task<(string Path, string Page)> ConfirmControlAsync(this HttpClient client, Guid agentId, string control) =>
        client.SubmitAsync(ConfirmControlPage(agentId, control), $"{control}-agent");

    /// <summary>
    /// Gives the signed-in user a Binance key for Live that Binance takes, as a user does: they
    /// prove who they are, and store the key on its page.
    /// </summary>
    public static async Task StoreLiveKeyAsync(this HttpClient client)
    {
        await client.ConfirmIdentityAsync();

        var (path, page) = await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        Assert.True(path == KeyBrowser.KeysPage, $"The Live key was not stored:{Environment.NewLine}{page}");
    }

    /// <summary>A page as a person reads it: with the characters that the markup writes as numbers.</summary>
    public static string Read(string page) => WebUtility.HtmlDecode(page);

    /// <summary>The symbols that the form on a page offers, in order.</summary>
    public static IReadOnlyList<string> SymbolsOfferedOn(string page)
    {
        var choice = System.Text.RegularExpressions.Regex.Match(
            page, "<select id=\"agent-symbol\".*?</select>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(choice.Success, $"The page has no choice of a symbol:{Environment.NewLine}{page}");

        return
        [
            .. System.Text.RegularExpressions.Regex.Matches(choice.Value, "<option value=\"([^\"]*)\"")
                .Select(option => option.Groups[1].Value),
        ];
    }
}

/// <summary>
/// Stands for the market data of the exchange where the pages of agents ask it which symbols a
/// market has. By default the Spot market has both symbols of the file of the tests, and the
/// Futures market has the first of them only, in either environment.
/// </summary>
internal sealed class StandInMarketData : IExchangeMarketData
{
    private readonly ConcurrentQueue<(Market Market, TradingMode Mode)> _asked = new();

    /// <summary>What the exchange says of the symbols of a market in an environment.</summary>
    public Func<Market, TradingMode, ExchangeSymbols> Symbols { get; set; } =
        (market, _) => market == Market.Spot ? Listed("BTCUSDT", "SOLUSDT") : Listed("BTCUSDT");

    /// <summary>The markets and environments it was asked about, in order.</summary>
    public IReadOnlyList<(Market Market, TradingMode Mode)> Asked => [.. _asked];

    /// <summary>A reading that succeeded: the market has these symbols, each priced in its last four letters.</summary>
    public static ExchangeSymbols Listed(params string[] symbols) =>
        new(
            ExchangeCallOutcome.Succeeded,
            [.. symbols.Select(symbol => new ExchangeSymbol(symbol, symbol[..^4], symbol[^4..], true, 0.01m, 0.001m, 0.001m, 1000m, 5m, StopOrders: true))],
            DateTimeOffset.UnixEpoch);

    /// <summary>A reading that failed: the exchange gave no answer.</summary>
    public static ExchangeSymbols NoAnswer => new(ExchangeCallOutcome.Failed, [], null);

    public Task<ExchangeSymbols> GetSymbolsAsync(Market market, TradingMode mode, CancellationToken cancellationToken)
    {
        _asked.Enqueue((market, mode));

        return Task.FromResult(Symbols(market, mode));
    }

    public Task<ExchangeCandles> GetCandlesAsync(
        Market market, TradingMode mode, string symbol, CandleInterval interval, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The pages of agents ask for no candles.");

    public Task<ExchangePrice> GetPriceAsync(Market market, TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The pages of agents ask for no price.");

    public Task<ExchangeMarkPrice> GetMarkPriceAsync(TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The pages of agents ask for no mark price.");

    public Task<ExchangeOpenInterest> GetOpenInterestAsync(TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The pages of agents ask for no open interest.");
}

/// <summary>
/// Stands for what keeps positions and orders, which is not built yet: it says that the agents a
/// test names hold an open position or open orders, keeps the times it was asked to close a
/// position, and answers as the test says. Unless a test says otherwise, a position that it is
/// asked to close is closed, and gone from then on.
/// </summary>
internal sealed class StandInExposure : IAgentExposure
{
    private readonly ConcurrentDictionary<Guid, bool> _positions = new();
    private readonly ConcurrentDictionary<Guid, bool> _orders = new();
    private readonly ConcurrentQueue<Guid> _closes = new();

    /// <summary>How closing the position of an agent ends: by default it is closed when there is one.</summary>
    public Func<Guid, PositionCloseOutcome>? CloseAnswer { get; set; }

    /// <summary>What a test does at the moment a close is asked for, before it is answered: to see what is stored by then.</summary>
    public Action<Guid>? OnClose { get; set; }

    /// <summary>The agents whose position it was asked to close, in order.</summary>
    public IReadOnlyList<Guid> Closes => [.. _closes];

    /// <summary>Says from now on that an agent has an open position.</summary>
    public void Holds(Guid agentId) => _positions[agentId] = true;

    /// <summary>Says from now on that an agent has open orders, and no position.</summary>
    public void HoldsOrders(Guid agentId) => _orders[agentId] = true;

    /// <summary>Says from now on that an agent holds nothing: its position was closed at the exchange.</summary>
    public void HoldsNothing(Guid agentId)
    {
        _positions.TryRemove(agentId, out _);
        _orders.TryRemove(agentId, out _);
    }

    public Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(_positions.ContainsKey(agentId) || _orders.ContainsKey(agentId));

    public Task<bool> HasOpenPositionAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(_positions.ContainsKey(agentId));

    public Task<PositionCloseOutcome> ClosePositionAtMarketAsync(Guid agentId, CancellationToken cancellationToken)
    {
        _closes.Enqueue(agentId);
        OnClose?.Invoke(agentId);

        var outcome = CloseAnswer?.Invoke(agentId)
            ?? (_positions.ContainsKey(agentId) ? PositionCloseOutcome.Closed : PositionCloseOutcome.NothingOpen);

        if (outcome == PositionCloseOutcome.Closed)
        {
            _positions.TryRemove(agentId, out _);
        }

        return Task.FromResult(outcome);
    }
}

/// <summary>The application as the tests of agents run it.</summary>
internal static class AgentsApplication
{
    /// <summary>
    /// The application with a stand-in for the market data of the exchange, and one for what
    /// keeps positions and orders when a test has one.
    /// </summary>
    public static AccountsApplication With(
        StandInMarketData market,
        TestClock? clock = null,
        StandInExposure? exposure = null,
        Func<SettingsJson, SettingsJson>? settings = null,
        Action<IWebHostBuilder>? host = null) =>
        new(
            clock: clock,
            settings: settings,
            host: builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IExchangeMarketData>(market);

                    if (exposure is not null)
                    {
                        services.AddSingleton<IAgentExposure>(exposure);
                    }
                });

                host?.Invoke(builder);
            });
}

/// <summary>Rows of agents that a test needs a user to have, without the pages that create them.</summary>
internal static class StoredAgentRows
{
    /// <summary>
    /// Gives a user an agent, stored as the application stores one, in the state that the test
    /// names, with the one change that says so. It is for a test that needs an agent in a state
    /// without the steps that lead there, or in a state that nothing leads to yet. Nobody is
    /// signed in for it, and nothing is recorded in the audit trail. The agent has a whole team,
    /// as <see cref="GiveTeamAsync"/> gives one, unless the test says that it has none.
    /// </summary>
    /// <returns>The id of the agent.</returns>
    public static async Task<Guid> GiveAgentAsync(
        this AccountsApplication application,
        Guid userId,
        DateTimeOffset at,
        string name = "Seeded",
        AgentState state = AgentState.Draft,
        Market market = Market.Spot,
        string symbol = "BTCUSDT",
        TradingMode mode = TradingMode.Demo,
        AgentStateCause cause = AgentStateCause.Created,
        bool team = true,
        Timeframe timeframe = Timeframe.OneHour)
    {
        var id = Guid.CreateVersion7();

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Add(new Agent(
            id,
            userId,
            name,
            mode,
            market,
            symbol,
            quoteAsset: "USDT",
            timeframe,
            allocatedCapital: 1000m,
            riskPerTradePercent: 1.5m,
            minRewardToRisk: 1.75m,
            leverageCap: 4,
            liquidationBuffer: 2.5m,
            dailyLossPercent: 5m,
            drawdownPercent: 20m,
            maxEntryDistancePercent: 0.8m,
            state,
            cause,
            stateChangedAt: at,
            createdAt: at));
        context.Add(new AgentStateChange(Guid.CreateVersion7(), id, userId, mode, state, cause, at));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (team)
        {
            await application.GiveTeamAsync(userId, id, at);
        }

        return id;
    }

    /// <summary>The model that <see cref="GiveTeamAsync"/> gives every role, unless a test names another: one that the file of the tests allows.</summary>
    public const string TeamModel = "claude-test-model";

    /// <summary>
    /// Gives an agent of a user a whole team, without the pages that choose one: every role has
    /// the same model, the catalog holds that model, and the user has a valid key for its
    /// provider that can use it. It is for a test that is about something else, and needs an
    /// agent that can be started. Nobody is signed in for it, and nothing is recorded.
    /// </summary>
    /// <returns>The id of the model in the catalog.</returns>
    public static async Task<Guid> GiveTeamAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset at,
        string provider = "claude",
        string model = TeamModel)
    {
        var modelId = Guid.Empty;

        foreach (var role in Enum.GetValues<Role>())
        {
            modelId = await application.GiveRoleAsync(userId, agentId, role, at, provider, model);
        }

        return modelId;
    }

    /// <summary>
    /// Gives one role of an agent a model, in place of the one it has, as
    /// <see cref="GiveTeamAsync"/> gives every role one: the catalog holds the model, and the
    /// user has a valid key for its provider that can use it.
    /// </summary>
    /// <returns>The id of the model in the catalog.</returns>
    public static async Task<Guid> GiveRoleAsync(
        this AccountsApplication application,
        Guid userId,
        Guid agentId,
        Role role,
        DateTimeOffset at,
        string provider = "claude",
        string model = TeamModel)
    {
        var modelId = await application.GiveModelAsync(provider, model, at);

        await application.LetKeyUseAsync(userId, provider, model, at);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        await context.Set<AgentRole>()
            .Where(given => given.AgentId == agentId && given.Role == role)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        context.Add(new AgentRole(agentId, userId, role, modelId));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return modelId;
    }

    /// <summary>
    /// Gives every agent that a user has now a whole team, as <see cref="GiveTeamAsync"/> gives
    /// one. It is for a test that creates its agents on their pages, where a user without a key
    /// for an AI provider is offered no model, and then starts them.
    /// </summary>
    public static async Task GiveTeamsAsync(this AccountsApplication application, Guid userId, DateTimeOffset at)
    {
        foreach (var agent in StoredAgent.Of(application.Database, userId))
        {
            await application.GiveTeamAsync(userId, agent.Id, at);
        }
    }

    /// <summary>
    /// Puts a model in the catalog, as a sync that found it in its provider's list does, unless
    /// the catalog holds it already.
    /// </summary>
    /// <returns>The id of the model in the catalog.</returns>
    public static async Task<Guid> GiveModelAsync(
        this AccountsApplication application, string provider, string identifier, DateTimeOffset at, string? displayName = null)
    {
        var code = new AiProviderCode(provider);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var known = await context.Set<AiModel>()
            .Where(model => model.Provider == code && model.Identifier == identifier)
            .Select(model => (Guid?)model.Id)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        if (known is { } id)
        {
            return id;
        }

        id = Guid.CreateVersion7();

        context.Add(new AiModel(
            id,
            code,
            identifier,
            displayName,
            contextTokens: null,
            outputTokens: null,
            releasedAt: null,
            deprecatedAt: null,
            retiresAt: null,
            AiModelStatus.Active,
            missedSyncs: 0,
            firstSeenAt: at,
            lastSeenAt: at));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    /// <summary>
    /// Makes the key of a user for an AI provider one that can use a model: the provider has
    /// listed the model to it, among whatever it listed before. A user who has no key for the
    /// provider is given one, valid as of the moment it was stored.
    /// </summary>
    public static async Task LetKeyUseAsync(
        this AccountsApplication application, Guid userId, string provider, string identifier, DateTimeOffset at)
    {
        var code = new AiProviderCode(provider);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var key = await context.Set<AiCredential>()
            .Where(credential => credential.UserId == userId && credential.Provider == code)
            .Select(credential => new { credential.Id, credential.AccessibleModels })
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        if (key is null)
        {
            await application.GiveAiKeyAsync(userId, provider, at, [identifier]);

            return;
        }

        string[] listed = [.. (key.AccessibleModels ?? []).Append(identifier).Distinct(StringComparer.Ordinal)];

        await context.Set<AiCredential>()
            .Where(credential => credential.Id == key.Id)
            .ExecuteUpdateAsync(
                row => row.SetProperty(credential => credential.AccessibleModels, listed), TestContext.Current.CancellationToken);
    }
}
