using System.ComponentModel.DataAnnotations;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Configuration;

/// <summary>
/// The runtime settings of the <c>Agents</c> section (Appendix D, NFR-PERF-01/02, FR-EXE-03): the evaluation deadline
/// per timeframe, how many evaluations run at once and the default entry timeout. The fair-use limits of the same
/// section are bound by the accounts module (<c>AgentLimitsOptions</c>); the binder ignores the keys it does not own.
/// </summary>
public sealed class AgentsOptions
{
    public const string SectionName = "Agents";

    /// <summary>Seconds from the candle close within which an evaluation must finish, keyed by timeframe label: 15m, 1H, 4H, 1D.</summary>
    public Dictionary<string, int> EvaluationDeadlineSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["15m"] = 90,
        ["1H"] = 180,
        ["4H"] = 300,
        ["1D"] = 300,
    };

    [Range(1, 1000)]
    public int MaxConcurrentEvaluations { get; set; } = 10;

    /// <summary>Default of the strategies' entry-timeout parameter: an unfilled limit entry is cancelled after this many candles (FR-EXE-03).</summary>
    [Range(1, 100)]
    public int DefaultEntryTimeoutCandles { get; set; } = 1;

    public static readonly IReadOnlyList<CandleInterval> Timeframes =
    [
        CandleInterval.FifteenMinutes, CandleInterval.OneHour, CandleInterval.FourHours, CandleInterval.OneDay,
    ];

    /// <summary>The key of a timeframe in <see cref="EvaluationDeadlineSeconds"/> (the SRS spelling: 15m, 1H, 4H, 1D).</summary>
    public static string KeyOf(CandleInterval timeframe) => timeframe switch
    {
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.OneHour => "1H",
        CandleInterval.FourHours => "4H",
        CandleInterval.OneDay => "1D",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Not an agent timeframe (FR-AGT-02)."),
    };

    public TimeSpan DeadlineFor(CandleInterval timeframe) =>
        TimeSpan.FromSeconds(EvaluationDeadlineSeconds.TryGetValue(KeyOf(timeframe), out var seconds) ? seconds : 0);

    public TimeSpan LongestDeadline => Timeframes.Select(DeadlineFor).Max();
}
