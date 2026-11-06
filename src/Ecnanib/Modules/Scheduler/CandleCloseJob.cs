using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Scheduler;

/// <summary>
/// Runs a timeframe's cycles at each of its candle closes (FR-CYC-01). Each timeframe is its own job, so it has its
/// own loop and schedule: a long queue at a daily close can't hold up the next 15-minute one. The names are what
/// each schedule finds its last run by, so they never change.
/// </summary>
internal abstract class CandleCloseJob(Timeframe timeframe, CycleScheduler scheduler) : IRecurringJob
{
    public abstract string Name { get; }

    public JobSchedule Schedule { get; } = scheduler.Schedule(timeframe);

    public TimeSpan Timeout => scheduler.Timeout(timeframe);

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        scheduler.RunAsync(timeframe, context, cancellationToken);
}

internal sealed class FifteenMinuteCycleJob(CycleScheduler scheduler) : CandleCloseJob(Timeframe.FifteenMinutes, scheduler)
{
    public override string Name => "cycles-15m";
}

internal sealed class HourlyCycleJob(CycleScheduler scheduler) : CandleCloseJob(Timeframe.OneHour, scheduler)
{
    public override string Name => "cycles-1h";
}

internal sealed class FourHourCycleJob(CycleScheduler scheduler) : CandleCloseJob(Timeframe.FourHours, scheduler)
{
    public override string Name => "cycles-4h";
}

internal sealed class DailyCycleJob(CycleScheduler scheduler) : CandleCloseJob(Timeframe.OneDay, scheduler)
{
    public override string Name => "cycles-1d";
}
