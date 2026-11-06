using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Scheduler;

/// <summary>
/// The cycle timings in the Agents section (SRS 7.3). Read() is the only way in. A missing value takes its default,
/// and an invalid one stops startup with a message that names it (CFG-03). The section's other settings belong to
/// Agents, and its approval windows to the decision cycle.
/// </summary>
internal sealed class SchedulerSettings
{
    public const string Section = "Agents";

    public const string CandleCloseDelayKey = Section + ":CandleCloseDelaySeconds";

    public const string MaxConcurrentCyclesKey = Section + ":MaxConcurrentCycles";

    public const string MaxJitterKey = Section + ":MaxCycleJitterSeconds";

    public const string CatchUpGraceKey = Section + ":CatchUpGraceMinutes";

    public const string CycleDeadlineKey = Section + ":CycleDeadlineMinutes";

    /// <summary>
    /// The most jitter there may be. A cycle should start within 30 seconds of its close and the delay
    /// (NFR-PERF-01), and the queue and the database need some of that.
    /// </summary>
    public const int MaximumJitterSeconds = 20;

    private static readonly Dictionary<Timeframe, int> DefaultGraceMinutes = new()
    {
        [Timeframe.FifteenMinutes] = 5,
        [Timeframe.OneHour] = 15,
        [Timeframe.FourHours] = 60,
        [Timeframe.OneDay] = 180,
    };

    /// <summary>NFR-PERF-02.</summary>
    private static readonly Dictionary<Timeframe, int> DefaultDeadlineMinutes = new()
    {
        [Timeframe.FifteenMinutes] = 3,
        [Timeframe.OneHour] = 10,
        [Timeframe.FourHours] = 20,
        [Timeframe.OneDay] = 30,
    };

    private readonly IReadOnlyDictionary<Timeframe, TimeSpan> _grace;
    private readonly IReadOnlyDictionary<Timeframe, TimeSpan> _deadlines;

    private SchedulerSettings(
        TimeSpan candleCloseDelay,
        int maxConcurrentCycles,
        TimeSpan maxJitter,
        IReadOnlyDictionary<Timeframe, TimeSpan> grace,
        IReadOnlyDictionary<Timeframe, TimeSpan> deadlines)
    {
        CandleCloseDelay = candleCloseDelay;
        MaxConcurrentCycles = maxConcurrentCycles;
        MaxJitter = maxJitter;
        _grace = grace;
        _deadlines = deadlines;
    }

    /// <summary>How long after a close its cycles are due, so the candle is final (FR-CYC-01).</summary>
    public TimeSpan CandleCloseDelay { get; }

    /// <summary>The most cycles that may run at once (FR-CYC-08).</summary>
    public int MaxConcurrentCycles { get; }

    /// <summary>The most a cycle waits before it queues, to spread exchange requests (FR-CYC-08, NFR-PERF-05).</summary>
    public TimeSpan MaxJitter { get; }

    /// <summary>How late a cycle may still start after the application was down (FR-CYC-04). Zero means never.</summary>
    public TimeSpan CatchUpGrace(Timeframe timeframe) => _grace[timeframe];

    /// <summary>How long after it's due a cycle may still execute a new entry (FR-CYC-06, NFR-PERF-02).</summary>
    public TimeSpan CycleDeadline(Timeframe timeframe) => _deadlines[timeframe];

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static SchedulerSettings Read(IConfiguration configuration)
    {
        var delay = SettingValues.WholeNumber(configuration, CandleCloseDelayKey, 10, 0, 300);
        var concurrency = SettingValues.WholeNumber(configuration, MaxConcurrentCyclesKey, 50, 1, 1000);
        var jitter = SettingValues.WholeNumber(configuration, MaxJitterKey, 10, 0, MaximumJitterSeconds);

        // The grace can't reach the close before, which is never caught up, and a deadline must end before the next close.
        var grace = SettingValues.MinutesPerTimeframe(configuration, CatchUpGraceKey, DefaultGraceMinutes, 0, CandleCloses.Interval);
        var deadlines = SettingValues.MinutesPerTimeframe(configuration, CycleDeadlineKey, DefaultDeadlineMinutes, 1, CandleCloses.Interval);

        return new SchedulerSettings(TimeSpan.FromSeconds(delay), concurrency, TimeSpan.FromSeconds(jitter), grace, deadlines);
    }
}

/// <summary>
/// Says at startup when cycles run. The host builds every hosted service before it starts any, and this one takes
/// <see cref="SchedulerSettings"/>, so an invalid setting stops startup before any migration runs (CFG-03).
/// </summary>
internal sealed class SchedulerStartup(SchedulerSettings settings, ILogger<SchedulerStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Decision cycles start {CandleCloseDelaySeconds} s after each candle closes, with up to {MaxCycleJitterSeconds} s of jitter, at most {MaxConcurrentCycles} at once",
            settings.CandleCloseDelay.TotalSeconds,
            settings.MaxJitter.TotalSeconds,
            settings.MaxConcurrentCycles);

        foreach (var timeframe in SettingValues.Timeframes)
        {
            logger.LogInformation(
                "{Timeframe} cycles have a deadline of {CycleDeadlineMinutes} min, and catch up within {CatchUpGraceMinutes} min",
                SettingValues.Label(timeframe),
                settings.CycleDeadline(timeframe).TotalMinutes,
                settings.CatchUpGrace(timeframe).TotalMinutes);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
