using Ecnanib.Kernel;
using Ecnanib.Modules.Scheduler.Contracts;

namespace Ecnanib.Modules.Scheduler;

/// <summary>The same closes and delay as <see cref="CandleSchedule"/>, which runs the cycles.</summary>
internal sealed class CycleTimes(SchedulerSettings settings, TimeProvider time) : ICycleTimes
{
    public DateTimeOffset NextCycleAt(Timeframe timeframe)
    {
        var delay = settings.CandleCloseDelay;
        var now = time.GetUtcNow();

        return CandleCloses.LatestAtOrBefore(timeframe, now - delay) + CandleCloses.Interval(timeframe) + delay;
    }
}
