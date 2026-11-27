using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Availability;

/// <summary>Why maintenance cannot be announced as it was asked for.</summary>
public enum MaintenanceProblem
{
    /// <summary>It would start now or earlier. Maintenance is announced before it starts.</summary>
    StartsTooSoon,

    /// <summary>It would end when it starts, or before.</summary>
    EndsBeforeItStarts,

    /// <summary>It would last longer than <c>Availability:LongestMaintenanceHours</c>.</summary>
    LastsTooLong,
}

/// <summary>
/// Writes the maintenance that is announced: it is announced, cancelled before it starts, and
/// ended while it is under way (NFR-REL-6). See "Announced maintenance" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each is written inside the transaction of whoever does it.</b> Only an operator announces
/// maintenance, and every action of an operator is recorded in the audit trail (FR-OPS-5). What
/// knows who an operator is and keeps that trail is a component, which this may not name: it
/// hands its context in, as it hands it to the trail, and the row is stored when the event is,
/// and not when it is not.
/// </para>
/// <para>
/// <b>Maintenance that has started is never taken away.</b> It can be cancelled before it
/// starts, which deletes its row, and ended early once it has started, which moves its end to
/// now. What is over stays as it was: a month that was measured is not measured anew.
/// </para>
/// <para>
/// <b>Nothing here acts on an agent.</b> An announcement is words on every page and time that
/// is left out of a measure. Nothing that trades reads it, so maintenance pauses no agent,
/// takes no order off, and closes no position.
/// </para>
/// </remarks>
public static class MaintenanceWindows
{
    /// <summary>What is wrong with maintenance from a moment to another, announced at a third, or null when nothing is.</summary>
    /// <param name="startsAt">From when.</param>
    /// <param name="endsAt">Until when.</param>
    /// <param name="now">When it is announced.</param>
    /// <param name="settings">How long maintenance lasts at the most.</param>
    public static MaintenanceProblem? Check(DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now, AvailabilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (startsAt <= now)
        {
            return MaintenanceProblem.StartsTooSoon;
        }

        if (endsAt <= startsAt)
        {
            return MaintenanceProblem.EndsBeforeItStarts;
        }

        return endsAt - startsAt > settings.LongestMaintenance ? MaintenanceProblem.LastsTooLong : null;
    }

    /// <summary>Announces maintenance, and says what was announced.</summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="startsAt">From when.</param>
    /// <param name="endsAt">Until when.</param>
    /// <param name="now">When it is announced.</param>
    /// <param name="settings">How long before its start it is announced in advance, and how long it lasts at the most.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">The context holds no transaction.</exception>
    /// <exception cref="ArgumentException"><see cref="Check"/> finds a problem with it.</exception>
    public static async Task<AnnouncedMaintenance> AnnounceAsync(
        EcnanibDbContext context,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset now,
        AvailabilitySettings settings,
        CancellationToken cancellationToken)
    {
        RequireTransaction(context);

        if (Check(startsAt, endsAt, now, settings) is { } problem)
        {
            throw new ArgumentException($"Maintenance cannot be announced as asked for: {problem}.", nameof(startsAt));
        }

        // In advance or not is decided now, and stays as it is decided.
        var window = new MaintenanceWindow(Guid.CreateVersion7(), startsAt, endsAt, now, startsAt - now >= settings.MaintenanceNotice);

        context.Set<MaintenanceWindow>().Add(window);
        await context.SaveChangesAsync(cancellationToken);

        return new AnnouncedMaintenance(window.Id, window.StartsAt, window.EndsAt, window.AnnouncedAt, window.InAdvance, UnderWay: false);
    }

    /// <summary>
    /// Cancels maintenance that has not started, and says whether this did it. It is false for
    /// maintenance that has started, that is over, or that was cancelled already.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="id">The id of its row.</param>
    /// <param name="now">When it is cancelled.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">The context holds no transaction.</exception>
    public static async Task<bool> CancelAsync(EcnanibDbContext context, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireTransaction(context);

        // Written only where it changes something, so the answer says whether this did it.
        return await context.Set<MaintenanceWindow>()
            .Where(window => window.Id == id && window.StartsAt > now)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Ends maintenance that is under way, and says whether this did it. It is false for
    /// maintenance that has not started, or that is over already.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="id">The id of its row.</param>
    /// <param name="now">When it is ended, which is its end from then on.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">The context holds no transaction.</exception>
    public static async Task<bool> EndAsync(EcnanibDbContext context, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireTransaction(context);

        return await context.Set<MaintenanceWindow>()
            .Where(window => window.Id == id && window.StartsAt <= now && window.EndsAt > now)
            .ExecuteUpdateAsync(row => row.SetProperty(window => window.EndsAt, now), cancellationToken) > 0;
    }

    private static void RequireTransaction(EcnanibDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Maintenance is announced, cancelled, and ended inside the transaction that records who did it, and this context holds none.");
        }
    }
}
