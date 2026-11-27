using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Performance;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Performance;

/// <summary>
/// What a run of the load test is given: how many agents run, what the limits of the queue are,
/// and how long the systems that stand in for the AI providers and for Binance take. See "The
/// load test" in docs/performance.md.
/// </summary>
/// <param name="Agents">How many agents run, and share the close.</param>
/// <param name="MaxPerProvider">
/// The most cycles that use one AI provider at once, in place of what the tracked file says, or
/// null for what it says.
/// </param>
/// <param name="ModelTakes">How long a model takes to answer a call.</param>
/// <param name="SpotCandleAfter">How long after a close Binance has begun the next candle on Spot, which is when it gives the candle of the close.</param>
/// <param name="FuturesCandleAfter">The same on Futures.</param>
/// <param name="Browsers">How many users have a browser open, beside the operator, which ask for screens while the cycles run.</param>
/// <param name="ScreenEvery">How long a browser waits between two screens.</param>
internal sealed record LoadPlan(
    int Agents,
    int? MaxPerProvider,
    TimeSpan ModelTakes,
    TimeSpan SpotCandleAfter,
    TimeSpan FuturesCandleAfter,
    int Browsers,
    TimeSpan ScreenEvery)
{
    /// <summary>The variable that asks for the load test: it takes minutes, and wants the machine to itself.</summary>
    public const string Variable = "ECNANIB_LOAD";

    /// <summary>How long after a shared candle boundary every cycle is to have started (NFR-PRF-3).</summary>
    public static readonly TimeSpan StartsWithin = TimeSpan.FromSeconds(30);

    /// <summary>How long after its close a cycle is to have ended, for 95 of 100 (NFR-PRF-2).</summary>
    public static readonly TimeSpan EndsWithin = TimeSpan.FromSeconds(90);

    /// <summary>How many agents of a user share the close: one on each symbol of each market, in the test environment.</summary>
    public const int AgentsOfAUser = 4;

    /// <summary>Whether the load test was asked for.</summary>
    public static bool AskedFor => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable));

    /// <summary>How many users the agents are of.</summary>
    public int Users => (Agents + AgentsOfAUser - 1) / AgentsOfAUser;

    /// <summary>
    /// The plan of the load test, as the variables of the environment say it. Without them it is
    /// the single instance of the specification: 500 agents, the limits of the tracked file,
    /// models that take a quarter of a minute, so that a cycle of three calls in a row takes 45
    /// seconds, and candles that Binance gives as late as it was ever measured to.
    /// </summary>
    public static LoadPlan OfTheEnvironment() => new(
        Agents: Whole("ECNANIB_LOAD_AGENTS") ?? 500,
        MaxPerProvider: Whole("ECNANIB_LOAD_MAX_PER_PROVIDER"),
        ModelTakes: Seconds("ECNANIB_LOAD_MODEL_SECONDS") ?? TimeSpan.FromSeconds(15),
        SpotCandleAfter: Seconds("ECNANIB_LOAD_SPOT_CANDLE_SECONDS") ?? TimeSpan.FromSeconds(5),
        FuturesCandleAfter: Seconds("ECNANIB_LOAD_FUTURES_CANDLE_SECONDS") ?? TimeSpan.FromSeconds(16),
        Browsers: Whole("ECNANIB_LOAD_BROWSERS") ?? 10,
        ScreenEvery: Seconds("ECNANIB_LOAD_SCREEN_SECONDS") ?? TimeSpan.FromMilliseconds(250));

    private static int? Whole(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } said ? int.Parse(said, CultureInfo.InvariantCulture) : null;

    private static TimeSpan? Seconds(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } said
            ? TimeSpan.FromSeconds(double.Parse(said, CultureInfo.InvariantCulture))
            : null;
}

/// <summary>
/// The application as the load test runs it: one instance on a database of its own, with the
/// agents and the keys of its users as rows, whose scheduler starts the cycles of a close by
/// itself, on a clock that runs. Everything is the application's own but the network: a stand-in
/// answers in the place of the AI providers, after the time that the plan says, and another in
/// the place of Binance, which gives the candle of the close as late as the plan says.
/// </summary>
/// <remarks>
/// <para>
/// What bears on how many agents an instance carries is as the tracked file has it, and not as
/// the file of the tests: the limits of the queue, the size of the data package, the stale
/// limits, the time limits of the calls, the share of Binance's limits, the breakers, and the
/// levels of the log.
/// </para>
/// <para>
/// The close is a midnight, when a candle of every timeframe closes, so the agents of all four
/// timeframes share it. The clock stands before it while the users and their agents are stored,
/// and is let run a few seconds before it: from then on a second of the clock is a second, and
/// what the application says of when a cycle started is how long that took.
/// </para>
/// </remarks>
internal sealed class LoadApplication : IDisposable
{
    /// <summary>The close that the agents share: a midnight in UTC.</summary>
    public static readonly DateTimeOffset Close = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The address of the operator, which the configuration file names.</summary>
    private const string Operator = "operator@load.example";

    // Between two quarter hours, so that no close is due while the agents are stored.
    private static readonly DateTimeOffset Before = Close.AddMinutes(-8);

    // How long before the close the clock is let run: the scheduler looks at it every second.
    private static readonly TimeSpan Lead = TimeSpan.FromSeconds(5);

    /// <summary>The team of a new agent, as the tracked file has it: each role at a provider of its own.</summary>
    private static readonly IReadOnlyDictionary<Role, string> Team = new Dictionary<Role, string>
    {
        [Role.MarketAnalyst] = "gpt",
        [Role.SentimentAnalyst] = "grok",
        [Role.Strategist] = "gemini",
        [Role.RiskManager] = "claude",
    };

    private readonly LoadPlan _plan;

    // The limits of the queue that the application runs with.
    private readonly (int MaxPerProvider, int MaxPerKey) _limits;
    private readonly List<StoredUser> _users = [];
    private readonly ConcurrentDictionary<Guid, LoadAgent> _agents = new();
    private readonly List<HttpClient> _browsers = [];

    public LoadApplication(LoadPlan plan)
    {
        _plan = plan;

        var tracked = Tracked();
        var cycles = tracked["Trading"]!["Cycles"]!;

        _limits = (plan.MaxPerProvider ?? cycles["MaxPerProvider"]!.GetValue<int>(), cycles["MaxPerKey"]!.GetValue<int>());

        Clock = new TestClock(Before);
        Providers = new LoadProviders(plan.ModelTakes);
        Binance = new LoadBinance(Clock, plan.SpotCandleAfter, plan.FuturesCandleAfter);

        Application = new AccountsApplication(
            clock: Clock,
            settings: file => AsTracked(file, tracked, plan),
            host: builder => builder.ConfigureServices(services =>
            {
                // The gateways themselves, with the test in the place of the network.
                services.AddHttpClient(AiProviderGatewayModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Providers);
                services.AddHttpClient(ExchangeGatewayModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Binance);
            }),
            runsCycles: true);

        // Started now, and not when the test first asks it for something.
        _ = Application.Contexts;
    }

    public AccountsApplication Application { get; }

    public TestClock Clock { get; }

    /// <summary>What answers in the place of the AI providers.</summary>
    public LoadProviders Providers { get; }

    /// <summary>What answers in the place of Binance.</summary>
    public LoadBinance Binance { get; }

    /// <summary>
    /// Stores the users with their keys and their agents, which run, and runs the close that
    /// they share: it returns when the cycles of the close are over, with what was measured.
    /// </summary>
    public async Task<LoadResult> RunAsync()
    {
        var seeding = Stopwatch.StartNew();
        await SeedAsync();
        seeding.Stop();

        await Until(
            () => Written(SchedulerLog.Area).Any(written => written.Template == SchedulerLog.Active),
            TimeSpan.FromSeconds(60),
            "the scheduler to run the background work");

        var browsers = await EnterAsync();

        // From here on the clock runs, and the scheduler starts the cycles when it shows the close.
        Clock.Set(Close - Lead);
        Clock.Run();

        // What the process takes of the machine from here to the end of the close: the
        // application, and with it the stand-ins and the browsers of the test.
        using var process = Process.GetCurrentProcess();
        var processorBefore = process.TotalProcessorTime;
        var watched = Stopwatch.StartNew();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var asking = browsers.Select((browser, number) => AskAsync(browser.Client, browser.Paths, number, stop.Token)).ToList();
        var limits = new ConcurrentDictionary<(string Name, TradingMode Environment), LimitUse>();
        var memory = new StrongBox<long>(Environment.WorkingSet);
        var watching = WatchAsync(limits, memory, stop.Token);

        // The run of the close begins within a second of it, and is over before the next close.
        await Until(() => Application.CycleLoad.Now() is not null, Lead + TimeSpan.FromSeconds(60), "the cycles of the close to be due");
        await Until(() => Application.CycleLoad.Now() is null, TimeSpan.FromMinutes(15), "the cycles of the close to be over");
        await Until(
            () => Written(CyclesLog.Area).Any(written => written.Template == CyclesLog.Over),
            TimeSpan.FromSeconds(30),
            "the summary of the close");

        watched.Stop();
        process.Refresh();

        var used = new MachineUse(process.TotalProcessorTime - processorBefore, watched.Elapsed, memory.Value);

        await stop.CancelAsync();

        var unanswered = (await Task.WhenAll(asking)).Sum();
        await watching;

        return Read(seeding.Elapsed, unanswered, [.. limits.Values], used);
    }

    public void Dispose()
    {
        foreach (var browser in _browsers)
        {
            browser.Dispose();
        }

        Application.Dispose();
    }

    /// <summary>The tracked file, which the build puts beside the tests.</summary>
    private static JsonNode Tracked() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.tracked.json")))!;

    /// <summary>The file of the tests, with what bears on the load as the tracked file has it.</summary>
    private static SettingsJson AsTracked(SettingsJson file, JsonNode tracked, LoadPlan plan)
    {
        string[] keys =
        [
            "Trading:Cycles", "Trading:DataPackage", "Trading:StaleSeconds", "Binance:LimitSharePercent", "Breakers",
            "Logging:MinimumLevel", "Logging:Overrides",
            "AiProviders:Claude:TimeoutSeconds", "AiProviders:Gemini:TimeoutSeconds", "AiProviders:GPT:TimeoutSeconds",
            "AiProviders:Grok:TimeoutSeconds",
        ];

        foreach (var key in keys)
        {
            var value = key.Split(':').Aggregate(tracked, (node, name) => node[name]!);

            file = file.With(key, value.DeepClone());
        }

        if (plan.MaxPerProvider is { } most)
        {
            file = file.With("Trading:Cycles:MaxPerProvider", most);
        }

        // Every user registers from the same place here, which is nowhere: more of them than a
        // service lets one address register in an hour.
        return file
            .With("Limits:Registration:PerClient", 100_000)
            .With("Limits:Registration:PerAddress", 100_000)
            .WithJson("Application:Operators", $"[ \"{Operator}\" ]");
    }

    /// <summary>The model of a role at its provider, which says the role: the stand-in answers by it.</summary>
    private static string ModelOf(string provider, Role role) => $"{provider}-{role.ToString().ToLowerInvariant()}-model";

    private async Task SeedAsync()
    {
        var at = Before.AddHours(-1);
        var cancellation = TestContext.Current.CancellationToken;

        // The catalog has the four models before any agent is given one, so that no two users
        // put the same model in it at once.
        foreach (var (role, provider) in Team)
        {
            await Application.GiveModelAsync(provider, ModelOf(provider, role), at);
        }

        // The users, one after the other: each verifies with the link of the latest message.
        await Application.RegisterVerifiedAsync(Operator);

        for (var number = 0; number < _plan.Users; number++)
        {
            _users.Add(await Application.RegisterVerifiedAsync($"user{number:0000}@load.example"));
        }

        // The symbols of the file, on both markets: what one user may have running at once in
        // the test environment, one agent on each. The timeframes take turns, so that every
        // symbol of every market has agents of all four, and all of them are due at midnight.
        string[] symbols = ["BTCUSDT", "SOLUSDT"];
        (Market Market, string Symbol)[] trades = [.. new[] { Market.Spot, Market.Futures }.SelectMany(market => symbols.Select(symbol => (market, symbol)))];
        var timeframes = Enum.GetValues<Timeframe>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, _users.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellation },
            async (number, _) =>
            {
                var user = _users[number];

                for (var slot = 0; slot < LoadPlan.AgentsOfAUser && (number * LoadPlan.AgentsOfAUser) + slot < _plan.Agents; slot++)
                {
                    var (market, symbol) = trades[slot];
                    var timeframe = timeframes[(number + slot) % timeframes.Length];

                    var agent = await Application.GiveAgentAsync(
                        user.Id,
                        at,
                        name: $"Load {slot + 1}",
                        state: AgentState.Running,
                        market: market,
                        symbol: symbol,
                        cause: AgentStateCause.UserStarted,
                        team: false,
                        timeframe: timeframe);

                    foreach (var (role, provider) in Team)
                    {
                        await Application.GiveRoleAsync(user.Id, agent, role, at, provider, ModelOf(provider, role));
                    }

                    _agents[agent] = new LoadAgent(agent, user.Id, market, symbol, timeframe);
                }
            });
    }

    /// <summary>Signs the users of the browsers in, and the operator, and says which screens each asks for.</summary>
    private async Task<IReadOnlyList<(HttpClient Client, IReadOnlyList<string> Paths)>> EnterAsync()
    {
        var entered = new List<(HttpClient, IReadOnlyList<string>)>();

        foreach (var user in _users.Take(_plan.Browsers))
        {
            var client = Application.CreateClient();
            _browsers.Add(client);

            await client.SignInAsync(user.Email);

            // The screens that a user has open while their agents work: the dashboard, the list
            // of the agents, an agent with its cycles, and the rest of the bar at the top.
            var agent = _agents.Values.Where(agent => agent.UserId == user.Id).OrderBy(agent => agent.Id).First().Id;

            entered.Add((client,
            [
                WebUiModule.DashboardPath,
                WebUiModule.AgentsPath,
                WebUiModule.AgentStatePath(agent),
                WebUiModule.AgentCyclesPath(agent),
                WebUiModule.DashboardPath,
                WebUiModule.ExperiencePath,
                WebUiModule.AgentStatePath(agent),
                WebUiModule.UsagePath,
                WebUiModule.CatalogPath,
                WebUiModule.NotificationsPath,
                WebUiModule.AccountPath,
            ]));
        }

        var operating = Application.CreateClient();
        _browsers.Add(operating);

        await operating.SignInAsync(Operator);

        entered.Add((operating, [WebUiModule.OperatorStatusPath]));

        return entered;
    }

    /// <summary>Asks for the screens of a browser, one after the other, until it is told to stop.</summary>
    /// <returns>How many answers were no success.</returns>
    private async Task<int> AskAsync(HttpClient client, IReadOnlyList<string> paths, int number, CancellationToken stop)
    {
        var failed = 0;

        try
        {
            // Not all at the same moment: each browser begins a little after the one before it.
            await Task.Delay(_plan.ScreenEvery * number / Math.Max(1, _plan.Browsers + 1), stop);

            for (var asked = number; ; asked++)
            {
                var path = paths[asked % paths.Count];

                // A screen that was asked for is waited for, also when the close is over
                // meanwhile: a browser that went away would be one more thing that is measured.
                using var answer = await client.GetAsync(path, TestContext.Current.CancellationToken);

                // The screen itself, and not the page that a browser is sent on to.
                if (!answer.IsSuccessStatusCode || answer.RequestMessage?.RequestUri?.AbsolutePath != path)
                {
                    failed++;
                }

                await Task.Delay(_plan.ScreenEvery, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return failed;
        }
    }

    /// <summary>
    /// Keeps the most that was used of each request limit of Binance, the most calls that waited
    /// for a turn, and the most memory that the process held.
    /// </summary>
    private async Task WatchAsync(
        ConcurrentDictionary<(string, TradingMode), LimitUse> most, StrongBox<long> memory, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                memory.Value = Math.Max(memory.Value, Environment.WorkingSet);

                foreach (var limit in Application.ExchangeLimits.Usage())
                {
                    most.AddOrUpdate(
                        (limit.Name, limit.Environment),
                        new LimitUse(limit.Name, limit.Environment, limit.Limit, limit.Allowed, limit.Used, limit.Waiting, limit.HeldUntil is not null),
                        (_, known) => known with
                        {
                            MostUsed = Math.Max(known.MostUsed, limit.Used),
                            MostWaiting = Math.Max(known.MostWaiting, limit.Waiting),
                            Held = known.Held || limit.HeldUntil is not null,
                        });
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The close is over.
        }
    }

    /// <summary>What the application said of the close, and what the test counted beside it.</summary>
    private LoadResult Read(TimeSpan seeding, int unanswered, IReadOnlyList<LimitUse> limits, MachineUse used)
    {
        var written = Written(CyclesLog.Area);

        var due = written.Where(one => one.Template == CyclesLog.Due).Sum(one => one["CycleCount"].GetInt32());

        var started = written
            .Where(one => one.Template == CyclesLog.Started)
            .Select(one =>
            {
                var agent = _agents[Guid.Parse(one.Text("AgentId")!)];

                return new StartedCycle(agent, TimeSpan.FromMilliseconds(one["StartDelayMs"].GetDouble()));
            })
            .ToList();

        var ended = written
            .Where(one => one.Template == CyclesLog.Ended)
            .Select(one => new EndedCycle(
                TimeSpan.FromMilliseconds(one["EndDelayMs"].GetDouble()), TimeSpan.FromMilliseconds(one["DurationMs"].GetDouble())))
            .ToList();

        var over = written.Single(one => one.Template == CyclesLog.Over);

        var summary = new CloseSummary(
            over.Level,
            TimeSpan.FromMilliseconds(over["DurationMs"].GetDouble()),
            over["Ended"].GetInt32(),
            over["EndedLate"].GetInt32(),
            over["Failed"].GetInt32(),
            over["OutOfTime"].GetInt32(),
            over["TooLate"].GetInt32(),
            over["WithoutCandle"].GetInt32(),
            over["NotReconciled"].GetInt32(),
            over["NotRunning"].GetInt32(),
            over["Stopped"].GetInt32());

        return new LoadResult(
            _plan,
            _limits,
            seeding,
            _agents.Count,
            _users.Count,
            due,
            started,
            ended,
            summary,
            Application.Database.Scalar<long>("SELECT count(*) FROM decision_cycles"),
            Providers.Calls,
            Binance.Calls,
            limits,
            Application.LoggedProblems.Read(),
            Application.ScreenTimes.Read(),
            unanswered,
            used);
    }

    /// <summary>What an area of the application has written so far, read while it is still writing.</summary>
    private IReadOnlyList<LoggedEvent> Written(string area)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return [.. Application.Events().Where(written => written.Area == area)];
            }
            catch (JsonException) when (attempt < 40)
            {
                // A line that is only half written yet.
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Waits for something, for longer than the tests of one thing wait: the load test takes minutes.</summary>
    private static async Task Until(Func<bool> condition, TimeSpan atMost, string what)
    {
        var waited = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(waited.Elapsed < atMost, $"Waited {atMost.TotalSeconds} seconds for {what}.");

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>An agent of the load test, with what its cycle is told apart by.</summary>
internal sealed record LoadAgent(Guid Id, Guid UserId, Market Market, string Symbol, Timeframe Timeframe);

/// <summary>A cycle that started, with how long after the close.</summary>
internal sealed record StartedCycle(LoadAgent Agent, TimeSpan After);

/// <summary>A cycle that ended, with how long after the close, and how long it ran.</summary>
internal sealed record EndedCycle(TimeSpan After, TimeSpan Ran);

/// <summary>What the application said of the cycles of the close, in its summary.</summary>
internal sealed record CloseSummary(
    string Level,
    TimeSpan Took,
    int Ended,
    int EndedLate,
    int Failed,
    int OutOfTime,
    int TooLate,
    int WithoutCandle,
    int NotReconciled,
    int NotRunning,
    int Stopped);

/// <summary>
/// What the process took of the machine from a few seconds before the close until its cycles
/// were over: the application, and with it the stand-ins and the browsers of the test. The
/// database is another process, and is not in it.
/// </summary>
/// <param name="ProcessorTime">The time that the processors worked for the process, all of them together.</param>
/// <param name="Over">How long that was measured over.</param>
/// <param name="MostMemory">The most memory that the process held, in bytes.</param>
internal sealed record MachineUse(TimeSpan ProcessorTime, TimeSpan Over, long MostMemory);

/// <summary>The most that was used of one request limit of Binance while the cycles ran.</summary>
internal sealed record LimitUse(string Name, TradingMode Environment, int Limit, int Allowed, int MostUsed, int MostWaiting, bool Held);

/// <summary>
/// Stands for the AI providers of the load test: it answers every role with a valid result of
/// its schema, in the form of the provider, after the time that a model takes. It keeps nothing
/// of a call but a count.
/// </summary>
/// <remarks>
/// The Strategist plans to open a position, and the Risk Manager vetoes it. So a cycle is the
/// three calls of its team in a row, the first of them two at once, which is the longest that a
/// cycle holds its places in the queue for, and no order is placed: the load is that of what
/// starts the cycles, runs the teams, and records them.
/// </remarks>
internal sealed class LoadProviders(TimeSpan takes) : HttpMessageHandler
{
    private static readonly Role[] Roles = Enum.GetValues<Role>();

    private int _calls;

    /// <summary>How many calls reached a provider.</summary>
    public int Calls => Volatile.Read(ref _calls);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);

        var provider = ModelCalls.ProviderOf(request);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var asked = request.RequestUri + body;

        // The model of the call names the role: in the address at one provider, and in the body
        // at the others.
        var role = Roles.Single(role => asked.Contains($"-{role.ToString().ToLowerInvariant()}-model", StringComparison.Ordinal));
        var offered = body.Contains("\"tools\"", StringComparison.Ordinal);

        var answer = role == Role.RiskManager ? TeamAnswers.Review(verdict: "veto", rationale: "The stop is too close.") : TeamAnswers.Of(role);

        await Task.Delay(takes, cancellationToken);

        return StandInSystem.Json(
            HttpStatusCode.OK, offered ? ModelCalls.Searched(provider, answer) : ModelCalls.Finished(provider, answer));
    }
}

/// <summary>
/// Stands for Binance in the load test: it answers the market data that the cycles read, and
/// gives the candle of a close only once it has begun the next candle, which it does as long
/// after the close as the plan says for the market. It keeps nothing of a call but a count.
/// </summary>
internal sealed class LoadBinance(TimeProvider clock, TimeSpan spotAfter, TimeSpan futuresAfter) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

    /// <summary>How many calls reached Binance, by their path.</summary>
    public IReadOnlyDictionary<string, int> Calls => new SortedDictionary<string, int>(_calls, StringComparer.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        _calls.AddOrUpdate(path, 1, (_, calls) => calls + 1);

        if (!path.EndsWith("/klines", StringComparison.Ordinal))
        {
            return Task.FromResult(StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request)));
        }

        // Its candles are as far behind the clock as it takes to begin the next one: until
        // then the candle that has closed by the clock is still in progress to Binance.
        var behind = path.StartsWith("/fapi/", StringComparison.Ordinal) ? futuresAfter : spotAfter;

        var rows = request.RequestUri.Query.TrimStart('?').Split('&')
            .Single(parameter => parameter.StartsWith("limit=", StringComparison.Ordinal))["limit=".Length..];

        // As many rows as were asked for, of which the last is the candle in progress.
        var candles = BinanceCalls.CandlesAt(
            clock.GetUtcNow() - behind, BinanceCalls.IntervalOf(request), closed: int.Parse(rows, CultureInfo.InvariantCulture) - 1);

        return Task.FromResult(StandInSystem.Json(HttpStatusCode.OK, candles));
    }
}

/// <summary>
/// What a run of the load test measured, and what it says of the two requirements: every cycle
/// at a shared candle boundary started within 30 seconds (NFR-PRF-3), and typical screens
/// respond within 300 ms at the 95th percentile (NFR-PRF-1).
/// </summary>
internal sealed record LoadResult(
    LoadPlan Plan,
    (int MaxPerProvider, int MaxPerKey) Limits,
    TimeSpan Seeding,
    int Agents,
    int Users,
    int Due,
    IReadOnlyList<StartedCycle> Started,
    IReadOnlyList<EndedCycle> Ended,
    CloseSummary Summary,
    long Recorded,
    int ModelCalls,
    IReadOnlyDictionary<string, int> BinanceCalls,
    IReadOnlyList<LimitUse> LimitsUsed,
    LoggedProblemCounts Problems,
    ScreenTimesReading Screens,
    int ScreensNotAnswered,
    MachineUse Machine)
{
    /// <summary>Where the report of a run is written: beside the tests as they were built.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "TestResults", "load");

    /// <summary>The cycles that started within the time that the specification gives them.</summary>
    public int StartedInTime => Started.Count(cycle => cycle.After <= LoadPlan.StartsWithin);

    /// <summary>The cycles that were to start, and did not: not in time, or not at all.</summary>
    public int NotStartedInTime => Agents - StartedInTime;

    /// <summary>Whether every agent that runs had its cycle started within 30 seconds of the close (NFR-PRF-3).</summary>
    public bool EveryCycleStartedInTime => Due == Agents && NotStartedInTime == 0;

    /// <summary>The cycles that an error ended, or that never ran.</summary>
    public int Lost =>
        Summary.Failed + Summary.OutOfTime + Summary.TooLate + Summary.WithoutCandle + Summary.NotReconciled + Summary.NotRunning
        + Summary.Stopped;

    /// <summary>Whether the screens answered within their target while the cycles ran (NFR-PRF-1).</summary>
    public bool ScreensMetTheirTarget => Screens.All.LastHour.MeetsTarget == true && ScreensNotAnswered == 0;

    /// <summary>What the run says of NFR-PRF-3, in one sentence.</summary>
    public string Verdict => EveryCycleStartedInTime
        ? $"Every cycle started within {LoadPlan.StartsWithin.TotalSeconds:0} seconds of the close: all {Agents} of them, the last after {Seconds(Started.Max(cycle => cycle.After))}."
        : $"{NotStartedInTime} of {Agents} cycles did not start within {LoadPlan.StartsWithin.TotalSeconds:0} seconds of the close: "
            + $"{StartedInTime} started in time, {Started.Count - StartedInTime} later, and {Agents - Started.Count} not at all.";

    /// <summary>Writes the report to its file, named after the plan, and says where.</summary>
    public string Write()
    {
        Directory.CreateDirectory(Folder);

        var file = Path.Combine(Folder, $"report-{Agents}-agents-{Limits.MaxPerProvider}-per-provider.md");

        File.WriteAllText(file, Report());

        return file;
    }

    /// <summary>The report of the run, as Markdown.</summary>
    public string Report()
    {
        var report = new StringBuilder();
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024 * 1024);

        report.AppendLine(Invariant($"# Load test: {Agents} running agents at one close"));
        report.AppendLine();
        report.AppendLine(Invariant($"Run on {DateTimeOffset.UtcNow:d MMMM yyyy, HH:mm} UTC."));
        report.AppendLine();
        report.AppendLine(Verdict);
        report.AppendLine();
        report.AppendLine(ScreensMetTheirTarget
            ? Invariant($"The screens answered within their target meanwhile: {ScreenTarget.Percent} of 100 within {Milliseconds(Screens.All.LastHour)}.")
            : Invariant($"The screens missed their target meanwhile: {Screens.All.LastHour.WithinTarget} of {Screens.All.LastHour.Count} answers were within {ScreenTarget.Within.TotalMilliseconds:0} ms, and {ScreensNotAnswered} were no success."));
        report.AppendLine();

        report.AppendLine("## What ran");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("|---|---|");
        report.AppendLine(Invariant($"| Machine | {Environment.ProcessorCount} processors, {memory:0} GB, {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}) |"));
        report.AppendLine(Invariant($"| Runtime | {RuntimeInformation.FrameworkDescription}, one instance of the application, PostgreSQL in a container on the same machine |"));
        report.AppendLine(Invariant($"| Agents that run | {Agents}, of {Users} users, each with the default team of four providers |"));
        report.AppendLine(Invariant($"| `Trading:Cycles:MaxPerProvider` | {Limits.MaxPerProvider} |"));
        report.AppendLine(Invariant($"| `Trading:Cycles:MaxPerKey` | {Limits.MaxPerKey} |"));
        report.AppendLine(Invariant($"| A model answers after | {Seconds(Plan.ModelTakes)} |"));
        report.AppendLine(Invariant($"| Binance gives the candle of the close after | {Seconds(Plan.SpotCandleAfter)} on Spot, {Seconds(Plan.FuturesCandleAfter)} on Futures |"));
        report.AppendLine(Invariant($"| Browsers | {Plan.Browsers} of the users and the operator, each asking for a screen every {Plan.ScreenEvery.TotalMilliseconds:0} ms |"));
        report.AppendLine(Invariant($"| Storing the users and their agents took | {Seconds(Seeding)} |"));
        report.AppendLine(Invariant($"| Processor time of the process over the close | {Seconds(Machine.ProcessorTime)} in {Seconds(Machine.Over)}: {Machine.ProcessorTime / Machine.Over:0.0} of {Environment.ProcessorCount} processors on average |"));
        report.AppendLine(Invariant($"| Memory of the process at the most | {Machine.MostMemory / (1024d * 1024):0} MB |"));
        report.AppendLine();

        report.AppendLine("## The cycles of the close (NFR-PRF-3, NFR-PRF-2)");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("|---|---|");
        report.AppendLine(Invariant($"| Due | {Due} |"));
        report.AppendLine(Invariant($"| Started | {Started.Count} |"));
        report.AppendLine(Invariant($"| Started within {LoadPlan.StartsWithin.TotalSeconds:0} seconds of the close | {StartedInTime} |"));
        report.AppendLine(Invariant($"| Started later | {Started.Count - StartedInTime} |"));
        report.AppendLine(Invariant($"| Not started | {Agents - Started.Count} |"));

        if (Started.Count > 0)
        {
            var after = Started.Select(cycle => cycle.After).Order().ToList();

            report.AppendLine(Invariant($"| The first started after | {Seconds(after[0])} |"));
            report.AppendLine(Invariant($"| Half had started after | {Seconds(At(after, 50))} |"));
            report.AppendLine(Invariant($"| 95 of 100 had started after | {Seconds(At(after, 95))} |"));
            report.AppendLine(Invariant($"| The last started after | {Seconds(after[^1])} |"));
        }

        report.AppendLine(Invariant($"| Ended within {LoadPlan.EndsWithin.TotalSeconds:0} seconds of the close | {Summary.Ended} |"));
        report.AppendLine(Invariant($"| Ended later | {Summary.EndedLate} |"));
        report.AppendLine(Invariant($"| Failed | {Summary.Failed} |"));
        report.AppendLine(Invariant($"| Stopped before the next close | {Summary.OutOfTime} |"));
        report.AppendLine(Invariant($"| Could not start in time, and were skipped | {Summary.TooLate} |"));
        report.AppendLine(Invariant($"| Had no candle | {Summary.WithoutCandle} |"));
        report.AppendLine(Invariant($"| Not reconciled, no longer running, or stopped with the application | {Summary.NotReconciled + Summary.NotRunning + Summary.Stopped} |"));

        if (Ended.Count > 0)
        {
            var ran = Ended.Select(cycle => cycle.Ran).Order().ToList();

            // What a cycle took beside the three calls of its team in a row: the application's own share.
            var own = ran.Select(time => time - (Plan.ModelTakes * 3)).ToList();

            report.AppendLine(Invariant($"| A cycle ran for | {Seconds(At(ran, 50))} for half, {Seconds(At(ran, 95))} for 95 of 100, {Seconds(ran[^1])} at the most |"));
            report.AppendLine(Invariant($"| Of that, beside the calls of its team | {Seconds(At(own, 50))} for half, {Seconds(At(own, 95))} for 95 of 100, {Seconds(own[^1])} at the most |"));
            report.AppendLine(Invariant($"| The last cycle ended after | {Seconds(Ended.Max(cycle => cycle.After))} |"));
        }

        report.AppendLine(Invariant($"| The run of the close took | {Seconds(Summary.Took)} |"));
        report.AppendLine(Invariant($"| Its summary in the log | {Summary.Level} |"));
        report.AppendLine(Invariant($"| Cycles recorded in the database | {Recorded} |"));
        report.AppendLine();

        if (Started.Count > 0)
        {
            report.AppendLine("When the cycles started, by what their candles are of:");
            report.AppendLine();
            report.AppendLine("| Market | Timeframe | Cycles | First after | Last after |");
            report.AppendLine("|---|---|---|---|---|");

            foreach (var group in Started.GroupBy(cycle => (cycle.Agent.Market, cycle.Agent.Timeframe)).OrderBy(group => group.Key))
            {
                report.AppendLine(Invariant(
                    $"| {group.Key.Market} | {group.Key.Timeframe.Code()} | {group.Count()} | {Seconds(group.Min(cycle => cycle.After))} | {Seconds(group.Max(cycle => cycle.After))} |"));
            }

            report.AppendLine();
        }

        report.AppendLine("## The calls");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("|---|---|");
        report.AppendLine(Invariant($"| Calls to the models | {ModelCalls} |"));
        report.AppendLine(Invariant($"| Calls to Binance | {BinanceCalls.Values.Sum()}: {string.Join(", ", BinanceCalls.Select(call => $"{call.Value} of `{call.Key}`"))} |"));

        foreach (var limit in LimitsUsed.OrderBy(limit => limit.Name, StringComparer.Ordinal).ThenBy(limit => limit.Environment))
        {
            report.AppendLine(Invariant(
                $"| Request weight, {limit.Name} ({limit.Environment}) | {limit.MostUsed} at the most, of {limit.Allowed} that the service allows itself and {limit.Limit} that Binance allows; {limit.MostWaiting} calls waited at the most{(limit.Held ? "; calls were held back" : string.Empty)} |"));
        }

        report.AppendLine();

        report.AppendLine("## The log");
        report.AppendLine();

        if (Problems.Areas.Count == 0)
        {
            report.AppendLine("No error and no warning was logged.");
        }
        else
        {
            report.AppendLine("| Area | Errors | Warnings |");
            report.AppendLine("|---|---|---|");

            foreach (var area in Problems.Areas)
            {
                report.AppendLine(Invariant($"| {area.Area ?? "Outside the application"} | {area.ErrorsLastDay} | {area.WarningsLastDay} |"));
            }
        }

        report.AppendLine();

        report.AppendLine("## The screens (NFR-PRF-1)");
        report.AppendLine();
        report.AppendLine(Invariant($"Measured at the server, by the application itself, while the cycles ran. {ScreensNotAnswered} answers were no success."));
        report.AppendLine();
        report.AppendLine(Invariant($"| Screen | Shown | Within {ScreenTarget.Within.TotalMilliseconds:0} ms | {ScreenTarget.Percent} of 100 within | Target |"));
        report.AppendLine("|---|---|---|---|---|");

        foreach (var screen in Screens.Screens.Prepend(Screens.All))
        {
            var times = screen.LastHour;

            report.AppendLine(Invariant(
                $"| {(screen.Route is { } route ? $"`{route}`" : "All screens")} | {times.Count} | {times.WithinTarget} | {Milliseconds(times)} | {(times.MeetsTarget switch { true => "Met", false => "Missed", null => "Not known" })} |"));
        }

        return report.ToString();
    }

    /// <summary>The time that so many of a hundred were within, of times in their order.</summary>
    private static TimeSpan At(IReadOnlyList<TimeSpan> ordered, int percent) =>
        ordered[Math.Max(0, (int)Math.Ceiling(ordered.Count * percent / 100d) - 1)];

    private static string Seconds(TimeSpan time) => Invariant($"{time.TotalSeconds:0.0} s");

    private static string Milliseconds(AnswerTimes times) => times switch
    {
        { Count: 0 } => "Not known",
        { BeyondLongest: true } => "more than a minute",
        _ => Invariant($"{times.Percentile!.Value.TotalMilliseconds:0} ms"),
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
