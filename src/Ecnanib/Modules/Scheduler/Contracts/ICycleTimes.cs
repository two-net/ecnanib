using Ecnanib.Kernel;

namespace Ecnanib.Modules.Scheduler.Contracts;

/// <summary>When decision cycles run (FR-CYC-01), for pages that tell the user, such as the wizard after it starts an agent (UC-01).</summary>
public interface ICycleTimes
{
    /// <summary>
    /// When the next cycle on <paramref name="timeframe"/> is due: the next candle close after now, aligned to UTC, plus
    /// <c>Agents:CandleCloseDelaySeconds</c>. A cycle may start up to <c>Agents:MaxCycleJitterSeconds</c> later (FR-CYC-08).
    /// A cycle due at this very moment is left out, because an agent that just became Active may miss its dispatch.
    /// </summary>
    DateTimeOffset NextCycleAt(Timeframe timeframe);
}
