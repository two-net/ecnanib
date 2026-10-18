using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Database.Configuration;

/// <summary>
/// The <c>retention</c> section of the system configuration file (Appendix D, NFR-DATA-03): how long log entries,
/// candles and deliberation transcripts are kept before the scheduled sweep deletes them (NFR-DATA-02, FR-LOG-06,
/// FR-DATA-05). Decisions, trades and experience statistics are never swept. All three periods live here rather
/// than under <c>logging</c> and <c>market data</c> because NFR-DATA-03 defines them together.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    [Range(1, 3650)]
    public int LogDays { get; set; } = 30;

    [Range(1, 3650)]
    public int CandleDays { get; set; } = 365;

    [Range(1, 3650)]
    public int DeliberationTranscriptDays { get; set; } = 90;

    /// <summary>How often the sweep runs.</summary>
    [Range(1, 1440)]
    public int SweepIntervalMinutes { get; set; } = 60;

    /// <summary>Rows deleted per statement; small batches keep locks and WAL short.</summary>
    [Range(100, 100_000)]
    public int DeleteBatchSize { get; set; } = 5000;

    public TimeSpan LogRetention => TimeSpan.FromDays(LogDays);

    public TimeSpan CandleRetention => TimeSpan.FromDays(CandleDays);

    public TimeSpan DeliberationTranscriptRetention => TimeSpan.FromDays(DeliberationTranscriptDays);

    public TimeSpan SweepInterval => TimeSpan.FromMinutes(SweepIntervalMinutes);
}
