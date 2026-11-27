using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Availability;

/// <summary>
/// The maintenance that is announced and not over, for the banner of every page and for the
/// page that an operator announces it on (NFR-REL-6). See "Announced maintenance" in
/// docs/reliability.md.
/// </summary>
/// <remarks>
/// It only reads. Maintenance is announced, cancelled, and ended by an operator, through what
/// records each in the audit trail: see <see cref="MaintenanceWindows"/>.
/// </remarks>
public interface IAnnouncedMaintenance
{
    /// <summary>
    /// The maintenance that a page tells of: what is under way, or else what starts next. Null
    /// when none is announced, or all that was is over.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AnnouncedMaintenance?> NextAsync(CancellationToken cancellationToken);

    /// <summary>All the maintenance that is not over, in the order that it starts in.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<AnnouncedMaintenance>> NotOverAsync(CancellationToken cancellationToken);
}

/// <summary>Maintenance that an operator announced, and that is not over.</summary>
/// <param name="Id">The id of its row, which says nothing of anyone.</param>
/// <param name="StartsAt">From when.</param>
/// <param name="EndsAt">Until when.</param>
/// <param name="AnnouncedAt">When it was announced.</param>
/// <param name="InAdvance">
/// Whether it was announced as long before its start as the configuration file asked for. What
/// the service is down for during it is left out of its availability only then (NFR-REL-1).
/// </param>
/// <param name="UnderWay">Whether it has started.</param>
public sealed record AnnouncedMaintenance(
    Guid Id,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset AnnouncedAt,
    bool InAdvance,
    bool UnderWay);

/// <summary>Reads the maintenance that is announced. See <see cref="IAnnouncedMaintenance"/>.</summary>
/// <remarks>
/// <b>Nothing is cached.</b> What is announced is read from its rows each time it is asked for,
/// so an announcement shows with the next page, on every instance of the application, and one
/// that was cancelled is gone with the next.
/// </remarks>
internal sealed class AnnouncedMaintenances(IDbContextFactory<EcnanibDbContext> contexts, TimeProvider clock) : IAnnouncedMaintenance
{
    public async Task<AnnouncedMaintenance?> NextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // What starts first of what is not over is what is under way, when anything is.
        var next = await NotOver(context, now).FirstOrDefaultAsync(cancellationToken);

        return next is null ? null : Shown(next, now);
    }

    public async Task<IReadOnlyList<AnnouncedMaintenance>> NotOverAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var windows = await NotOver(context, now).ToListAsync(cancellationToken);

        return [.. windows.Select(window => Shown(window, now))];
    }

    private static IQueryable<MaintenanceWindow> NotOver(EcnanibDbContext context, DateTimeOffset now) =>
        context.Set<MaintenanceWindow>()
            .AsNoTracking()
            .Where(window => window.EndsAt > now)
            .OrderBy(window => window.StartsAt)
            .ThenBy(window => window.Id);

    private static AnnouncedMaintenance Shown(MaintenanceWindow window, DateTimeOffset now) =>
        new(window.Id, window.StartsAt, window.EndsAt, window.AnnouncedAt, window.InAdvance, window.StartsAt <= now);
}
