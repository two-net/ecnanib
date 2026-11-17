using System.Collections.Concurrent;
using System.Text.Json;
using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>What the job that starts the cycles writes to the log, and the area it writes under.</summary>
internal static class CyclesLog
{
    public const string Area = "Ecnanib.DecisionEngine.DecisionCyclesJob";

    public const string Due = "{CycleCount} cycles are due for the close at {ClosedAt}, with candles of {GroupCount} symbols and timeframes";
    public const string Started = "The cycle of agent {AgentId} for the close at {ClosedAt} ({Timeframe}) started {StartDelayMs} ms after the close";
    public const string Ended = "The cycle of agent {AgentId} for the close at {ClosedAt} ended {EndDelayMs} ms after the close, in {DurationMs} ms";
    public const string Failed = "The cycle of agent {AgentId} for the close at {ClosedAt} failed after {DurationMs} ms, with no decision";
    public const string StoppedForTheNextClose =
        "The cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms: the next close is near";
    public const string NoLongerRuns = "Agent {AgentId} no longer runs, so it gets no cycle for the close at {ClosedAt}";
    public const string NotReconciled =
        "Agent {AgentId} is not reconciled with the exchange yet, so it gets no cycle for the close at {ClosedAt}";
    public const string NotStartedInTime =
        "{CycleCount} cycles of the close at {ClosedAt} could not start within {StartsWithinMinutes} minutes of it, and are skipped";
    public const string StillRunning =
        "{CycleCount} cycles of the close at {ClosedAt} still run {EndsBeforeSeconds} seconds before the next close, and are stopped";
    public const string RunTooLate =
        "The cycles of the close at {ClosedAt} could not start within {StartsWithinMinutes} minutes of it, so none starts";
    public const string NoCandle =
        "The exchange did not give the candle of {Symbol} on {Market} in {TradingMode} that closed at {ClosedAt} ({Timeframe}) " +
        "within {CandleWaitSeconds} seconds of the close: {ExchangeCallOutcome}. {CycleCount} cycles are skipped";
    public const string Over =
        "The cycles of the close at {ClosedAt} are over after {DurationMs} ms: {Ended} ended within {TargetSeconds} seconds of the close " +
        "and {EndedLate} later, {Failed} failed, {OutOfTime} were stopped before the next close, {TooLate} could not start in time, " +
        "{WithoutCandle} had no candle, {NotReconciled} were not reconciled, {NotRunning} no longer ran, and {Stopped} were stopped with the application";
}

/// <summary>
/// The application as the tests of the cycles run it: with the job that starts the cycles, on a
/// clock that the test sets, with candles that the test's exchange gives, and with a stand-in for
/// what a cycle does once it has started, unless the test says that its cycles run their teams.
/// </summary>
/// <remarks>
/// A test starts the cycles of a close in one of two ways. It runs the job itself, with
/// <see cref="RunAsync"/>, for a close of its choosing, and can wait for the cycles of that close
/// to be over. Or it moves the clock over a quarter hour, and the scheduler runs the job: the
/// clock then starts between two quarter hours, so that the job is not due as the application
/// starts.
/// </remarks>
internal sealed class CyclesApplication : IDisposable
{
    /// <param name="now">What the clock shows as the application starts.</param>
    /// <param name="maxPerProvider">The most cycles that use one provider at once, when it is not what the file of the tests says.</param>
    /// <param name="maxPerKey">The most cycles that use one key at once, when it is not what the file of the tests says.</param>
    /// <param name="storedAgents">
    /// Whether the agents are those of the database, read as the application reads them. Unless
    /// a test says so, they are the ones that it gives <see cref="Agents"/>, which need no rows.
    /// </param>
    /// <param name="reconciliation">What reconciles, when the test has something that does. Nothing does yet.</param>
    /// <param name="database">A database that an earlier application of the test has used.</param>
    /// <param name="buildsPackages">
    /// Whether a cycle that starts does what the application has it do, which is to build its
    /// data package and to run the team of its agent, on an exchange of the test's that has all
    /// the market data: <see cref="Market"/>. Unless a test says so, the inside of a cycle is
    /// <see cref="Cycles"/>, and the exchange is <see cref="Candles"/>, which is asked for the
    /// candles of a close alone.
    /// </param>
    /// <param name="providers">
    /// What answers in the place of the AI providers, for cycles that call their models through
    /// the gateway itself, with the stored keys of their users, and that are recorded in the
    /// database: the agents are then those of the database. Unless a test says so, the models,
    /// the keys, and the records of a cycle that runs its team are those of <see cref="Team"/>.
    /// </param>
    /// <param name="holdings">
    /// What every agent holds, for cycles that build their data packages: a position, for a test
    /// of a plan that closes, reverses, or adjusts one. Unless a test says so, an agent holds
    /// nothing, as in the application.
    /// </param>
    /// <param name="settings">Other settings of the file of the tests, such as a stale limit.</param>
    /// <param name="halt">
    /// What keeps the global halt, for a test of a plan that is checked while one is on. Nothing
    /// keeps one yet, so unless a test says so no halt is on, as in the application.
    /// </param>
    public CyclesApplication(
        DateTimeOffset now,
        int? maxPerProvider = null,
        int? maxPerKey = null,
        bool storedAgents = false,
        StandInReconciliation? reconciliation = null,
        TestDatabase? database = null,
        bool buildsPackages = false,
        StandInSystem? providers = null,
        StandInHoldings? holdings = null,
        Func<SettingsJson, SettingsJson>? settings = null,
        StandInHalt? halt = null)
    {
        buildsPackages |= providers is not null;
        storedAgents |= providers is not null;

        Clock = new TestClock(now);
        Candles = new StandInCandles(Clock);
        Market = buildsPackages ? new StandInMarket(Clock) : null;
        Agents = storedAgents ? null : new StandInRunningAgents();
        Team = buildsPackages && providers is null ? new StandInTeam() : null;

        Application = new AccountsApplication(
            clock: Clock,
            database: database,
            settings: file =>
            {
                file = maxPerProvider is { } perProvider ? file.With("Trading:Cycles:MaxPerProvider", perProvider) : file;
                file = maxPerKey is { } perKey ? file.With("Trading:Cycles:MaxPerKey", perKey) : file;

                return settings?.Invoke(file) ?? file;
            },
            host: builder => builder.ConfigureServices(services =>
            {
                if (Market is null)
                {
                    services.AddSingleton<IExchangeMarketData>(Candles);
                    services.AddSingleton<IDecisionCycle>(Cycles);
                }
                else
                {
                    // The inside of a cycle is the application's own, which builds a data package
                    // and runs the team.
                    services.AddSingleton<IExchangeMarketData>(Market);

                    if (providers is not null)
                    {
                        // The gateway itself, with the test in the place of the providers.
                        services.AddHttpClient(AiProviderGatewayModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => providers);
                    }

                    Team?.Behind(services);
                }

                if (Agents is not null)
                {
                    services.AddSingleton<IRunningAgents>(Agents);
                }

                if (reconciliation is not null)
                {
                    services.AddSingleton<IReconciliation>(reconciliation);
                }

                if (holdings is not null)
                {
                    services.AddSingleton<IAgentHoldings>(holdings);
                }

                if (halt is not null)
                {
                    services.AddSingleton<IGlobalHalt>(halt);
                }
            }),
            runsCycles: true);

        // Started now, and not when a test first asks it for something.
        _ = Application.Contexts;
    }

    private bool _stopped;

    public AccountsApplication Application { get; }

    public TestClock Clock { get; }

    /// <summary>What stands for the inside of a cycle.</summary>
    public StandInCycles Cycles { get; } = new();

    /// <summary>What stands for the market data of the exchange.</summary>
    public StandInCandles Candles { get; }

    /// <summary>
    /// What stands for the market data of the exchange where the cycles build their data
    /// packages, in place of <see cref="Candles"/>: it has what a package is built from.
    /// </summary>
    public StandInMarket? Market { get; }

    /// <summary>The agents that run, where they are not those of the database.</summary>
    public StandInRunningAgents? Agents { get; }

    /// <summary>
    /// What stands for the models, the keys, and the records of the cycles, where the cycles run
    /// the teams of their agents and the agents need no rows.
    /// </summary>
    public StandInTeam? Team { get; }

    /// <summary>What stands for the AI providers, where the cycles run the teams of their agents.</summary>
    public StandInModels Models => Team!.Models;

    /// <summary>The records of the cycles, where the cycles run the teams of their agents.</summary>
    public StandInCycleRecords Records => Team!.Records;

    /// <summary>What stands for what places the orders of a cycle, and keeps what it was handed.</summary>
    public StandInPlanOrders Orders => Team!.Orders;

    /// <summary>Runs the job for a close, as the scheduler does. It ends when the cycles of the close are over.</summary>
    public Task RunAsync(DateTimeOffset closedAt, CancellationToken? cancellationToken = null) =>
        Application.RunCyclesAsync(closedAt, cancellationToken);

    /// <summary>
    /// Waits until the scheduler of the application runs the background work. It works out when
    /// each job is next due from what the clock shows then, so a test moves the clock only after.
    /// </summary>
    public Task SchedulerStartedAsync() =>
        Wait.UntilAsync(
            () => Written(SchedulerLog.Area).Any(written => written.Template == SchedulerLog.Active),
            "the scheduler to run the background work");

    /// <summary>What the job has written so far, read while the application is still writing.</summary>
    public IReadOnlyList<LoggedEvent> Written(string area = CyclesLog.Area)
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

    /// <summary>The events of one template that the job has written so far.</summary>
    public IReadOnlyList<LoggedEvent> Written(string area, string template) =>
        [.. Written(area).Where(written => written.Template == template)];

    /// <summary>The summary of the cycles of a close, which the job writes when they are over.</summary>
    public LoggedEvent SummaryOf(DateTimeOffset closedAt) =>
        Assert.Single(
            Written(CyclesLog.Area, CyclesLog.Over),
            written => written["ClosedAt"].GetDateTimeOffset() == closedAt);

    /// <summary>
    /// Stops the application, as a shutdown does, while its cycles are as they are: a cycle that
    /// is held is not let go first. It returns when the application has stopped.
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        Application.Dispose();
    }

    public void Dispose()
    {
        // A cycle that is still held is let go, so that a run of the test's own ends as well.
        Cycles.ReleaseAll();
        Team?.Models.ReleaseAll();

        if (!_stopped)
        {
            Application.Dispose();
        }
    }
}

/// <summary>How long a test waits for the cycles of a close to be over.</summary>
internal static class CycleWaits
{
    /// <summary>The run of the job, which a test does not wait for longer than half a minute.</summary>
    public static Task Within(this Task run) =>
        run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
}

/// <summary>
/// Stands for what a cycle does once it has started, for a test of what starts the cycles: it
/// keeps what each cycle was handed, in the order the cycles started, and how many ran at once, for each provider
/// and for each key of a user. A test can hold the cycles until it lets them go, so that they run
/// at the same time for as long as it says, and can have a cycle fail.
/// </summary>
internal sealed class StandInCycles : IDecisionCycle
{
    private readonly Lock _gate = new();
    private readonly List<CycleStart> _started = [];
    private readonly List<Guid> _stopped = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _held = [];
    private readonly Dictionary<string, int> _running = [];
    private readonly Dictionary<string, int> _most = [];
    private bool _holds;

    /// <summary>Whether a cycle that starts goes on until the test lets it go.</summary>
    public bool Holds
    {
        get
        {
            lock (_gate)
            {
                return _holds;
            }
        }

        set
        {
            lock (_gate)
            {
                _holds = value;
            }
        }
    }

    /// <summary>The error that a cycle ends with, when the test gives it one.</summary>
    public Func<CycleStart, Exception?>? Fails { get; set; }

    /// <summary>What each cycle was handed, in the order the cycles started.</summary>
    public IReadOnlyList<CycleStart> Started
    {
        get
        {
            lock (_gate)
            {
                return [.. _started];
            }
        }
    }

    /// <summary>The agents whose cycles started, in that order.</summary>
    public IReadOnlyList<Guid> StartedFor => [.. Started.Select(cycle => cycle.Agent.Id)];

    /// <summary>The agents whose cycles were stopped through their token, in that order.</summary>
    public IReadOnlyList<Guid> Stopped
    {
        get
        {
            lock (_gate)
            {
                return [.. _stopped];
            }
        }
    }

    /// <summary>The most cycles that ran at once.</summary>
    public int MostAtOnce => MostOf("all");

    /// <summary>The most cycles that used a provider at once.</summary>
    public int MostAtOnceWith(string provider) => MostOf(provider);

    /// <summary>The most cycles that used the key of a user for a provider at once.</summary>
    public int MostAtOnceWith(Guid userId, string provider) => MostOf($"{userId}/{provider}");

    public async Task RunAsync(CycleStart cycle, CancellationToken cancellationToken)
    {
        // What the cycle takes a place with: each provider of its roles once, and the key of
        // its user for that provider.
        var providers = cycle.Roles.Select(role => role.Provider.Value).Distinct().ToList();
        string[] uses = ["all", .. providers, .. providers.Select(provider => $"{cycle.Agent.UserId}/{provider}")];

        TaskCompletionSource? held = null;

        lock (_gate)
        {
            _started.Add(cycle);

            foreach (var use in uses)
            {
                var running = _running.GetValueOrDefault(use) + 1;
                _running[use] = running;
                _most[use] = Math.Max(_most.GetValueOrDefault(use), running);
            }

            if (_holds)
            {
                held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held[cycle.Agent.Id] = held;
            }
        }

        try
        {
            if (Fails?.Invoke(cycle) is { } error)
            {
                throw error;
            }

            if (held is not null)
            {
                await held.Task.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _stopped.Add(cycle.Agent.Id);
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                foreach (var use in uses)
                {
                    _running[use]--;
                }
            }
        }
    }

    /// <summary>Lets the cycle of an agent end, which started and is held.</summary>
    public void Release(Guid agentId)
    {
        lock (_gate)
        {
            Assert.True(_held.Remove(agentId, out var held), $"No cycle of agent {agentId} is held.");
            held!.TrySetResult();
        }
    }

    /// <summary>Lets every cycle end that is held, and holds none from now on.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            _holds = false;

            foreach (var held in _held.Values)
            {
                held.TrySetResult();
            }

            _held.Clear();
        }
    }

    /// <summary>Waits until the cycles of a number of agents have started.</summary>
    public Task StartedAsync(int cycles) =>
        Wait.UntilAsync(() => Started.Count >= cycles, $"{cycles} cycles to start");

    private int MostOf(string use)
    {
        lock (_gate)
        {
            return _most.GetValueOrDefault(use);
        }
    }
}

/// <summary>
/// Stands for the market data of the exchange where the cycles ask it for candles. Unless a test
/// says otherwise, the exchange has closed every candle that has closed by the clock of the
/// test: the newest candle that it gives closed at the latest boundary of its length.
/// </summary>
internal sealed class StandInCandles(TimeProvider clock) : IExchangeMarketData
{
    private readonly ConcurrentQueue<AskedCandles> _asked = new();

    /// <summary>What the exchange answers an ask with, when it is not the candles through the latest close: null is those.</summary>
    public Func<AskedCandles, ExchangeCandles?>? Answer { get; set; }

    /// <summary>The asks, in order.</summary>
    public IReadOnlyList<AskedCandles> Asked => [.. _asked];

    /// <summary>A reading that succeeded: closed candles, oldest first, of which the newest closed at a time.</summary>
    public static ExchangeCandles Through(DateTimeOffset closedAt, CandleInterval interval, int candles = 3)
    {
        var length = LengthOf(interval);

        return new ExchangeCandles(
            ExchangeCallOutcome.Succeeded,
            [
                .. Enumerable.Range(0, candles)
                    .Select(candle => closedAt - (length * (candles - candle)))
                    .Select(openedAt => new ExchangeCandle(openedAt, openedAt + length, 100m, 110m, 90m, 105m, 12.5m)),
            ],
            closedAt);
    }

    /// <summary>A reading that failed: the exchange gave no answer.</summary>
    public static ExchangeCandles NoAnswer => new(ExchangeCallOutcome.Failed, [], null);

    /// <summary>The latest close of a candle of a length at or before a time.</summary>
    public static DateTimeOffset LatestClose(DateTimeOffset at, CandleInterval interval) =>
        new(at.UtcTicks - (at.UtcTicks % LengthOf(interval).Ticks), TimeSpan.Zero);

    public Task<ExchangeCandles> GetCandlesAsync(
        Market market, TradingMode mode, string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        var asked = new AskedCandles(market, mode, symbol, interval);
        _asked.Enqueue(asked);

        return Task.FromResult(Answer?.Invoke(asked) ?? Through(LatestClose(clock.GetUtcNow(), interval), interval));
    }

    public Task<ExchangePrice> GetPriceAsync(Market market, TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        throw new NotSupportedException("What starts a cycle asks for no price.");

    public Task<ExchangeSymbols> GetSymbolsAsync(Market market, TradingMode mode, CancellationToken cancellationToken) =>
        throw new NotSupportedException("What starts a cycle asks for no symbols.");

    public Task<ExchangeMarkPrice> GetMarkPriceAsync(TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        throw new NotSupportedException("What starts a cycle asks for no mark price.");

    public Task<ExchangeOpenInterest> GetOpenInterestAsync(TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        throw new NotSupportedException("What starts a cycle asks for no open interest.");

    private static TimeSpan LengthOf(CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
        CandleInterval.OneHour => TimeSpan.FromHours(1),
        CandleInterval.FourHours => TimeSpan.FromHours(4),
        CandleInterval.OneDay => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "What starts a cycle asks for no such candles."),
    };
}

/// <summary>One ask for candles: of a symbol and a length of a candle, on a market in an environment.</summary>
internal sealed record AskedCandles(Market Market, TradingMode Mode, string Symbol, CandleInterval Interval);

/// <summary>
/// Stands for the agents that run, where a test needs many of them, or of many users, and none of
/// their rows: it says the agents that the test gave it, each with a team, and no longer says an
/// agent that the test stopped.
/// </summary>
internal sealed class StandInRunningAgents : IRunningAgents
{
    private static readonly DateTimeOffset First = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentDictionary<Guid, RunningAgent> _agents = new();
    private int _given;

    /// <summary>
    /// Gives a user an agent that runs. The ids of the agents ascend in the order they are given.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="timeframe">The timeframe of the agent.</param>
    /// <param name="symbol">Its symbol.</param>
    /// <param name="market">Its market.</param>
    /// <param name="mode">Its environment.</param>
    /// <param name="providers">
    /// The provider of each role of its team, in the order of the roles. One provider is that of
    /// all four roles, and none is Claude for all four.
    /// </param>
    /// <returns>The id of the agent.</returns>
    public Guid Give(
        Guid userId,
        Timeframe timeframe = Timeframe.FifteenMinutes,
        string symbol = "BTCUSDT",
        Market market = Market.Spot,
        TradingMode mode = TradingMode.Demo,
        params string[] providers)
    {
        var number = Interlocked.Increment(ref _given);
        var id = Guid.CreateVersion7(First.AddSeconds(number));

        var roles = Enum.GetValues<Role>();
        var ofRoles = providers.Length switch
        {
            0 => [.. roles.Select(_ => "claude")],
            1 => [.. roles.Select(_ => providers[0])],
            _ => providers,
        };

        _agents[id] = new RunningAgent(
            new Agent(
                id,
                userId,
                $"Agent {number}",
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
                AgentState.Running,
                AgentStateCause.UserStarted,
                stateChangedAt: First,
                createdAt: First),
            [
                .. ofRoles.Select((provider, role) => new AgentRoleView(
                    roles[role], Guid.CreateVersion7(), new AiProviderCode(provider), $"{provider}-test-model", null)),
            ]);

        return id;
    }

    /// <summary>The agent no longer runs: its user paused or stopped it.</summary>
    public void Stop(Guid agentId) => Assert.True(_agents.TryRemove(agentId, out _), $"No agent {agentId} runs.");

    public Task<IReadOnlyList<RunningAgent>> OfTimeframesAsync(
        IReadOnlyCollection<Timeframe> timeframes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunningAgent>>(
        [
            // Latest first: the order of the cycles is not the order that the agents are read in.
            .. _agents.Values
                .Where(running => timeframes.Contains(running.Agent.Timeframe))
                .OrderByDescending(running => running.Agent.Id),
        ]);

    public Task<RunningAgent?> FindAsync(Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(_agents.GetValueOrDefault(agentId));
}

/// <summary>
/// Stands for what reconciles the records of an agent with the exchange, which is not built: an
/// agent is reconciled unless the test says that it is not yet.
/// </summary>
internal sealed class StandInReconciliation : IReconciliation
{
    private readonly ConcurrentDictionary<Guid, bool> _notYet = new();
    private readonly ConcurrentQueue<Guid> _asked = new();

    /// <summary>The agents that it was asked about, in order.</summary>
    public IReadOnlyList<Guid> Asked => [.. _asked];

    /// <summary>The agent is not reconciled yet.</summary>
    public void NotYet(Guid agentId) => _notYet[agentId] = true;

    /// <summary>The agent is reconciled now.</summary>
    public void Done(Guid agentId) => _notYet.TryRemove(agentId, out _);

    public Task<bool> HasReconciledAsync(RunningAgent agent, CancellationToken cancellationToken)
    {
        _asked.Enqueue(agent.Agent.Id);

        return Task.FromResult(!_notYet.ContainsKey(agent.Agent.Id));
    }
}
