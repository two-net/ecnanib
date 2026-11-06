using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Jobs;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Scheduler;

/// <summary>
/// The application with its own database and clock, no jitter, the candle-close jobs for a test to run, and fakes in
/// place of the agents the scheduler lists and of the decision cycle itself. Logs reach the database, at every level.
/// </summary>
internal sealed class SchedulerApp : IAsyncDisposable
{
    public const string FifteenMinutes = "FifteenMinuteCycleJob";
    public const string OneHour = "HourlyCycleJob";
    public const string FourHours = "FourHourCycleJob";
    public const string OneDay = "DailyCycleJob";

    /// <summary>The default <c>Agents:CandleCloseDelaySeconds</c>.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    /// <summary>A Monday, so every timeframe has a close at its midnight.</summary>
    public static readonly DateTimeOffset Day = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public SchedulerApp(PostgresFixture postgres, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?> { ["Agents:MaxCycleJitterSeconds"] = "0" };

        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        Logged = new LoggedApp(
            postgres.NewConnectionString(),
            all,
            services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.Replace(ServiceDescriptor.Singleton<IActiveAgents>(Agents));
                services.Replace(ServiceDescriptor.Singleton<IDecisionCycles>(Cycles));
            },
            manualJobs: [FifteenMinutes, OneHour, FourHours, OneDay]);
    }

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public TestClock Clock { get; } = new();

    public FakeActiveAgents Agents { get; } = new();

    public FakeCycles Cycles { get; } = new();

    public LoggedApp Logged { get; }

    public AppFactory App => Logged.App;

    public string ConnectionString => App.ConnectionString();

    public IRecurringJob Job(string className) => App.ManualJob(className);

    /// <summary>Moves the application's clock to <paramref name="time"/>, which then runs on from there.</summary>
    public void SetClock(DateTimeOffset time) => Clock.Advance(time - Clock.GetUtcNow());

    /// <summary>
    /// Runs a job the way the runner would at <paramref name="time"/>, and returns its context, which holds what it
    /// stored. A test that stops it passes a context of its own.
    /// </summary>
    public async Task<JobContext> RunAtAsync(string className, DateTimeOffset time, JobContext? context = null)
    {
        SetClock(time);

        context ??= new JobContext(CancellationToken.None);
        await Job(className).RunAsync(context, Cancellation);

        return context;
    }

    /// <summary>Each cycle's run, as status and details, once all <paramref name="count"/> have finished.</summary>
    public async Task<List<string>> FinishedCyclesAsync(int count)
    {
        const string Finished = "FROM job_runs WHERE job_name = 'decision-cycle' AND finished_at IS NOT NULL";

        await LoggedApp.UntilAsync(async () => await Sql.ScalarAsync<long>(ConnectionString, $"SELECT count(*) {Finished}", Cancellation) >= count);

        return await Sql.ColumnAsync(ConnectionString, $"SELECT concat_ws(' ', status, details, correlation_id) {Finished} ORDER BY id", Cancellation);
    }

    /// <summary>Waits until the log table holds every one of <paramref name="texts"/>, and returns it.</summary>
    public async Task<string> UntilLoggedAsync(params string[] texts)
    {
        var table = "";

        await LoggedApp.UntilAsync(async () =>
        {
            table = await Logged.TableTextAsync(Cancellation);

            return texts.All(text => table.Contains(text, StringComparison.Ordinal));
        });

        return table;
    }

    public async ValueTask DisposeAsync()
    {
        // A held cycle would otherwise hold up shutdown for its whole drain.
        Cycles.OpenAll();
        await Logged.DisposeAsync();
    }
}

/// <summary>Stands in for Agents: the Active agents of each timeframe are whatever a test says.</summary>
internal sealed class FakeActiveAgents : IActiveAgents
{
    private readonly ConcurrentDictionary<Timeframe, ActiveAgent[]> _agents = new();

    /// <summary>Adds new agents on <paramref name="timeframe"/>, each its own user's unless <paramref name="userId"/> is given.</summary>
    public ActiveAgent[] Add(Timeframe timeframe, int count = 1, Guid? userId = null)
    {
        var added = Enumerable.Range(0, count).Select(_ => new ActiveAgent(Guid.NewGuid(), userId ?? Guid.NewGuid())).ToArray();

        _agents.AddOrUpdate(timeframe, added, (_, existing) => [.. existing, .. added]);

        return added;
    }

    public Task<IReadOnlyList<ActiveAgent>> ListAsync(Timeframe timeframe, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ActiveAgent>>(_agents.GetValueOrDefault(timeframe, []));
}

/// <summary>Stands in for the decision cycle: records each start, and holds or fails the agents a test chooses.</summary>
internal sealed class FakeCycles : IDecisionCycles
{
    private readonly ConcurrentQueue<CycleRequest> _started = new();
    private readonly ConcurrentDictionary<Guid, Gate> _holds = new();
    private readonly ConcurrentDictionary<Guid, byte> _failing = new();

    /// <summary>Every cycle that started, in the order they started.</summary>
    public IReadOnlyList<CycleRequest> Started => [.. _started];

    /// <summary>From now on, the agent's cycles wait at the gate until a test opens it.</summary>
    public Gate Hold(Guid agentId) => _holds.GetOrAdd(agentId, _ => new Gate());

    public void Fail(Guid agentId) => _failing[agentId] = 0;

    public void OpenAll()
    {
        foreach (var gate in _holds.Values)
        {
            gate.Open();
        }
    }

    public Task UntilStartedAsync(int count) => LoggedApp.UntilAsync(() => ValueTask.FromResult(_started.Count >= count));

    public async Task RunAsync(CycleRequest request, JobContext context, CancellationToken cancellationToken)
    {
        _started.Enqueue(request);

        if (_failing.ContainsKey(request.AgentId))
        {
            throw new InvalidOperationException("A test cycle failed");
        }

        if (_holds.TryGetValue(request.AgentId, out var gate))
        {
            await gate.PassAsync();
        }
    }
}
