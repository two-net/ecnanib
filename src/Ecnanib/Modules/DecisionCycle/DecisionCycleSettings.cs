using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// The decision cycle's settings in the Agents section. Read() is the only way in. A missing value takes its default,
/// and an invalid one stops startup with a message that names it (CFG-03).
/// </summary>
internal sealed class DecisionCycleSettings
{
    public const string CycleErrorsBeforePauseKey = "Agents:CycleErrorsBeforePause";

    public const string ApprovalWindowKey = "Agents:ApprovalWindowMinutes";

    public const int DefaultCycleErrorsBeforePause = 3;

    public const int MaximumCycleErrorsBeforePause = 100;

    /// <summary>SRS 7.3.</summary>
    private static readonly Dictionary<Timeframe, int> DefaultApprovalWindowMinutes = new()
    {
        [Timeframe.FifteenMinutes] = 5,
        [Timeframe.OneHour] = 20,
        [Timeframe.FourHours] = 60,
        [Timeframe.OneDay] = 240,
    };

    private readonly IReadOnlyDictionary<Timeframe, TimeSpan> _approvalWindows;

    private DecisionCycleSettings(int cycleErrorsBeforePause, IReadOnlyDictionary<Timeframe, TimeSpan> approvalWindows)
    {
        CycleErrorsBeforePause = cycleErrorsBeforePause;
        _approvalWindows = approvalWindows;
    }

    /// <summary>
    /// How many cycles in a row may end without a decision before the agent goes to Error. Zero means never.
    /// </summary>
    public int CycleErrorsBeforePause { get; }

    /// <summary>
    /// How long a Confirm agent's order waits for its user's approval before it expires (FR-APR-03). It ends before the
    /// next candle closes, so a limit entry approved late still has time to rest.
    /// </summary>
    public TimeSpan ApprovalWindow(Timeframe timeframe) => _approvalWindows[timeframe];

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static DecisionCycleSettings Read(IConfiguration configuration)
    {
        var errors = SettingValues.WholeNumber(
            configuration, CycleErrorsBeforePauseKey, DefaultCycleErrorsBeforePause, 0, MaximumCycleErrorsBeforePause);
        var windows = SettingValues.MinutesPerTimeframe(
            configuration, ApprovalWindowKey, DefaultApprovalWindowMinutes, 1, TimeframeLength.Of);

        return new DecisionCycleSettings(errors, windows);
    }
}
