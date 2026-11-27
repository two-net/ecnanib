using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Availability;

/// <summary>
/// Reads how available the service was from what <see cref="Uptime"/> wrote and from the
/// maintenance that was announced in advance (NFR-REL-1). See <see cref="IAvailability"/> and
/// "Availability" in docs/reliability.md.
/// </summary>
/// <remarks>
/// Nothing is kept: a month is worked out from its rows each time it is asked for, by
/// <see cref="MonthAvailability.Of"/>. The rows are few: one for each start and each break of
/// the service, and one for each announcement.
/// </remarks>
internal sealed class AvailabilityMeasure(
    IDbContextFactory<EcnanibDbContext> contexts,
    AvailabilitySettings settings,
    TimeProvider clock) : IAvailability
{
    public async Task<AvailabilityReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var today = now.UtcDateTime;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var from = new DateTimeOffset(lastMonth.Year, lastMonth.Month, 1, 0, 0, 0, TimeSpan.Zero);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var firstUp = await context.Set<UpSpan>()
            .AsNoTracking()
            .OrderBy(span => span.BeganAt)
            .Select(span => (DateTimeOffset?)span.BeganAt)
            .FirstOrDefaultAsync(cancellationToken);

        var spans = await context.Set<UpSpan>()
            .AsNoTracking()
            .Where(span => span.BeatAt > from)
            .Select(span => new { span.BeganAt, span.BeatAt })
            .ToListAsync(cancellationToken);

        // Only what was announced in advance is left out of the measure (NFR-REL-1).
        var windows = await context.Set<MaintenanceWindow>()
            .AsNoTracking()
            .Where(window => window.InAdvance && window.EndsAt > from)
            .Select(window => new { window.StartsAt, window.EndsAt })
            .ToListAsync(cancellationToken);

        var up = spans.Select(span => Uptime.StretchOf(span.BeganAt, span.BeatAt, now)).ToList();
        var maintenance = windows.Select(window => new Stretch(window.StartsAt, window.EndsAt)).ToList();

        return new AvailabilityReading(
            settings.TargetPercent,
            [
                MonthAvailability.Of(thisMonth, now, firstUp, up, maintenance, settings.TargetPercent),
                MonthAvailability.Of(lastMonth, now, firstUp, up, maintenance, settings.TargetPercent),
            ]);
    }
}
