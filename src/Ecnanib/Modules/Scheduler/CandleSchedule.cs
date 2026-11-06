using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Scheduler;

/// <summary>
/// A run at each close of <paramref name="timeframe"/>'s candles, <paramref name="delay"/> later, so the candle is
/// final (FR-CYC-01). The runner keeps the last start in job_runs, so a restart doesn't lose the schedule.
/// </summary>
internal sealed class CandleSchedule(Timeframe timeframe, TimeSpan delay) : JobSchedule
{
    private readonly TimeSpan _interval = CandleCloses.Interval(timeframe);

    /// <remarks>
    /// <para>
    /// A job that has never run waits for the next close rather than running at once, so a new installation
    /// doesn't run a cycle for a close it wasn't there for.
    /// </para>
    /// <para>
    /// Otherwise the last run handled the last close at or before its start less the delay, and the next close
    /// after that is due. When the application was down, that's in the past, and the runner runs it once, however
    /// many closes were missed. The run then decides whether to catch up (FR-CYC-04).
    /// </para>
    /// </remarks>
    public override DateTimeOffset NextRun(DateTimeOffset? lastStart, DateTimeOffset now)
    {
        if (lastStart is not { } last)
        {
            var due = CandleCloses.LatestAtOrBefore(timeframe, now - delay) + delay;

            return due == now ? now : due + _interval;
        }

        return CandleCloses.LatestAtOrBefore(timeframe, last - delay) + _interval + delay;
    }
}
