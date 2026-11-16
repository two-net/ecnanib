using System.Collections.Concurrent;
using System.Net;
using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
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
    int LeverageCap,
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
                   risk_per_trade_percent, leverage_cap, daily_loss_percent, drawdown_percent, max_entry_distance_percent,
                   state, state_cause, state_changed_at, created_at
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
                reader.GetInt32(10),
                reader.GetDecimal(11),
                reader.GetDecimal(12),
                reader.GetDecimal(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetFieldValue<DateTimeOffset>(16),
                reader.GetFieldValue<DateTimeOffset>(17)));
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
        string leverageCap = "4",
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
        ("LeverageCap", leverageCap),
        ("DailyLossPercent", dailyLoss),
        ("DrawdownPercent", drawdown),
        ("MaxEntryDistancePercent", entryDistance),
    ];

    /// <summary>What a form brings, with one field as something else.</summary>
    public static (string Name, string Value)[] With(this (string Name, string Value)[] fields, string name, string value) =>
        [.. fields.Select(field => field.Name == name ? (name, value) : field)];

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
            [.. symbols.Select(symbol => new ExchangeSymbol(symbol, symbol[..^4], symbol[^4..], true, 0.01m, 0.001m, 0.001m, 1000m, 5m))],
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
/// test names hold an open position or open orders.
/// </summary>
internal sealed class StandInExposure : IAgentExposure
{
    private readonly ConcurrentDictionary<Guid, bool> _holding = new();

    /// <summary>Says from now on that an agent has an open position.</summary>
    public void Holds(Guid agentId) => _holding[agentId] = true;

    public Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(_holding.ContainsKey(agentId));
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
    /// names. Nothing starts or stops an agent yet, so an agent that is not a draft is one that
    /// a test stored. Nobody is signed in for it, and nothing is recorded.
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
        TradingMode mode = TradingMode.Demo)
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
            Timeframe.OneHour,
            allocatedCapital: 1000m,
            riskPerTradePercent: 1.5m,
            leverageCap: 4,
            dailyLossPercent: 5m,
            drawdownPercent: 20m,
            maxEntryDistancePercent: 0.8m,
            state,
            AgentStateCause.Created,
            stateChangedAt: at,
            createdAt: at));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }
}
