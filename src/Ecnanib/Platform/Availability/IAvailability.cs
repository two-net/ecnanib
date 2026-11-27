namespace Ecnanib.Platform.Availability;

/// <summary>
/// How available the service was, held against the target of the configuration file: the share
/// of a month that it was up for, with announced maintenance left out (NFR-REL-1). It is for the
/// operator status screen. See "Availability" in docs/reliability.md.
/// </summary>
public interface IAvailability
{
    /// <summary>
    /// The month that runs and the one before it, in that order, as the database says them now.
    /// Every instance of the application answers the same.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AvailabilityReading> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The availability of the service, by the month.</summary>
/// <param name="TargetPercent">What a month is held to, in percent: <c>Availability:TargetPercent</c>.</param>
/// <param name="Months">The month that runs, and the one before it.</param>
public sealed record AvailabilityReading(decimal TargetPercent, IReadOnlyList<MonthAvailability> Months);

/// <summary>The availability of the service in one month of the calendar, in UTC.</summary>
/// <param name="Month">The first day of the month.</param>
/// <param name="MeasuredFrom">
/// From when the month was measured: its beginning, or the moment that the service first said
/// that it was up when that was later. Null when nothing of the month was measured.
/// </param>
/// <param name="MeasuredUntil">Until when it was measured: its end, or now when it still runs. Null when nothing was.</param>
/// <param name="Measured">How long the service was to be up for: the time that was measured, without the announced maintenance.</param>
/// <param name="Down">How long of that nothing said that it was up for.</param>
/// <param name="Maintenance">How long the maintenance lasted that was announced in advance, and is left out.</param>
/// <param name="Percent">The share of <paramref name="Measured"/> that the service was up for, in percent. Null when nothing was measured: it is not known then, and is no 0 and no 100.</param>
/// <param name="MeetsTarget">Whether that is the target or more. Null when it is not known.</param>
public sealed record MonthAvailability(
    DateOnly Month,
    DateTimeOffset? MeasuredFrom,
    DateTimeOffset? MeasuredUntil,
    TimeSpan Measured,
    TimeSpan Down,
    TimeSpan Maintenance,
    decimal? Percent,
    bool? MeetsTarget)
{
    /// <summary>
    /// Works a month out from when the service was up and when maintenance was announced for.
    /// It reads nothing, so it is the same for the same times, whenever it is asked.
    /// </summary>
    /// <param name="month">The first day of the month.</param>
    /// <param name="now">The moment that the month is asked about. What comes after it is not measured yet.</param>
    /// <param name="firstUp">When the service first said that it was up, or null when it never did.</param>
    /// <param name="up">When the service was up. They may overlap, and reach out of the month.</param>
    /// <param name="maintenance">The maintenance that was announced in advance. It may overlap, and reach out of the month.</param>
    /// <param name="targetPercent">What the month is held to, in percent.</param>
    public static MonthAvailability Of(
        DateOnly month,
        DateTimeOffset now,
        DateTimeOffset? firstUp,
        IEnumerable<Stretch> up,
        IEnumerable<Stretch> maintenance,
        decimal targetPercent)
    {
        ArgumentNullException.ThrowIfNull(up);
        ArgumentNullException.ThrowIfNull(maintenance);

        var begins = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var ends = begins.AddMonths(1);

        // What was before the service first said that it was up was not measured: a month that
        // the measure began in is held to what was measured of it, and not to the days before.
        var from = firstUp is { } first && first > begins ? first : begins;
        var until = now < ends ? now : ends;

        if (firstUp is null || from >= until)
        {
            return new MonthAvailability(month, null, null, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null, null);
        }

        var period = new Stretch(from, until);
        var announced = Stretches.Within(Stretches.Joined(maintenance), period);
        var toBeUp = Stretches.Without(period, announced);
        var wasUp = Stretches.Within(Stretches.Joined(up), toBeUp);

        var measured = Stretches.Length(toBeUp);
        var down = measured - Stretches.Length(wasUp);

        if (measured <= TimeSpan.Zero)
        {
            // All that was measured was announced maintenance. Nothing says how available that was.
            return new MonthAvailability(month, from, until, TimeSpan.Zero, TimeSpan.Zero, Stretches.Length(announced), null, null);
        }

        var percent = 100m * (measured - down).Ticks / measured.Ticks;

        return new MonthAvailability(month, from, until, measured, down, Stretches.Length(announced), percent, percent >= targetPercent);
    }
}
