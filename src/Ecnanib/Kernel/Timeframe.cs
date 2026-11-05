namespace Ecnanib.Kernel;

/// <summary>
/// An agent's candle timeframe (FR-AGT-02): 15m, 1H, 4H or 1D. A cycle runs when one of its candles
/// closes, aligned to UTC (FR-CYC-01). Agents, the scheduler, the decision cycle and market data all use
/// it. Rows will store the name, so a value is never renamed.
/// </summary>
public enum Timeframe
{
    FifteenMinutes,
    OneHour,
    FourHours,
    OneDay,
}
