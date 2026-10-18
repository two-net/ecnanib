namespace Ecnanib.Database.Retention;

/// <summary>Rows deleted by one retention sweep, and how many of the three targets failed.</summary>
internal sealed record RetentionSweepResult(int LogEntries, int Candles, int DeliberationTranscripts, int Failures)
{
    public int TotalDeleted => LogEntries + Candles + DeliberationTranscripts;
}
