namespace Ecnanib.Execution.Configuration;

/// <summary>
/// The <c>Execution</c> section (beyond Appendix D): how protective orders are retried before a position is flattened
/// (FR-EXE-05), the spot stop-limit offset, the budget of the candle-close management step, the age a row must have
/// before reconciliation touches it (FR-EXE-08) and the fill tracker's start delay.
/// </summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>Attempts to place or replace a protective order before the position is closed at market (FR-EXE-05).</summary>
    public int ProtectiveOrderAttempts { get; set; } = 3;

    public int ProtectiveOrderRetryDelayMs { get; set; } = 1000;

    /// <summary>How far below the stop (above, for a buy) a spot stop-loss-limit order's limit price sits, so it fills through a fast move.</summary>
    public decimal SpotStopLimitOffsetPercent { get; set; } = 0.5m;

    /// <summary>Time the pre-council management step may spend on reads and reconciliation before the council is asked; must stay under the shortest evaluation deadline.</summary>
    public int ManageBudgetSeconds { get; set; } = 20;

    /// <summary>Orders and trades younger than this are left to the evaluation that is placing them (FR-EXE-08).</summary>
    public int ReconcileAfterSeconds { get; set; } = 30;

    /// <summary>Staggered after the other hosted jobs.</summary>
    public int FillTrackerStartDelaySeconds { get; set; } = 6;

    public TimeSpan ProtectiveOrderRetryDelay => TimeSpan.FromMilliseconds(ProtectiveOrderRetryDelayMs);

    public decimal SpotStopLimitOffsetRate => SpotStopLimitOffsetPercent / 100m;

    public TimeSpan ManageBudget => TimeSpan.FromSeconds(ManageBudgetSeconds);

    public TimeSpan ReconcileAfter => TimeSpan.FromSeconds(ReconcileAfterSeconds);

    public TimeSpan FillTrackerStartDelay => TimeSpan.FromSeconds(FillTrackerStartDelaySeconds);
}
