namespace Ecnanib.Database.Entities;

/// <summary>
/// A "no new entry" window in UTC (FR-STR-05): recurring weekly (<see cref="StartDay"/>/<see cref="StartTime"/> to
/// <see cref="EndDay"/>/<see cref="EndTime"/>) or one-off (<see cref="StartAt"/> to <see cref="EndAt"/>).
/// </summary>
public sealed class EntryWindow
{
    public long Id { get; set; }

    public Guid AgentId { get; set; }

    public Agent Agent { get; set; } = null!;

    public EntryWindowKind Kind { get; set; }

    public DateTimeOffset? StartAt { get; set; }

    public DateTimeOffset? EndAt { get; set; }

    public DayOfWeek? StartDay { get; set; }

    public TimeOnly? StartTime { get; set; }

    public DayOfWeek? EndDay { get; set; }

    public TimeOnly? EndTime { get; set; }
}
