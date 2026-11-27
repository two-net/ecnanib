using Ecnanib.Platform.Availability;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// Announces maintenance for an operator, cancels it, and ends it (NFR-REL-6). See
/// <see cref="IMaintenanceAnnouncements"/>, and "Announced maintenance" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is here because of what it uses.</b> What is announced is of the service, and the
/// platform keeps it: see <see cref="MaintenanceWindows"/>. Who may announce it, and the trail
/// that says who did, are of this component, which the platform may not name. So the platform
/// writes its row inside the transaction that this opens, and the event is stored with the row.
/// </para>
/// <para>
/// <b>Everything is asked again.</b> The page that calls this carries the operator policy, and
/// each member asks <see cref="OperatorAccess.IsOperator"/> of the user of its request all the
/// same (NFR-SEC-3).
/// </para>
/// <para>
/// <b>One transaction, under the lock of the account.</b> The row of the operator is locked as
/// everything that records an action of a user locks it: the event points at it, and a deletion
/// of the account comes before this or waits for it.
/// </para>
/// </remarks>
internal sealed class MaintenanceAnnouncements(
    IDbContextFactory<EcnanibDbContext> contexts,
    AvailabilitySettings settings,
    TimeProvider clock,
    ILogger<MaintenanceAnnouncements> logger) : IMaintenanceAnnouncements
{
    public async Task<MaintenanceAnnouncement> AnnounceAsync(HttpContext request, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MayAsk(request))
        {
            return MaintenanceAnnouncement.Refused;
        }

        var operatorId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAccountAsync(context, operatorId, cancellationToken))
        {
            return MaintenanceAnnouncement.Refused;
        }

        // Held against the moment that it is written at, under the lock: what is announced
        // starts after that moment.
        var now = clock.GetUtcNow();

        if (MaintenanceWindows.Check(startsAt, endsAt, now, settings) is { } problem)
        {
            return new MaintenanceAnnouncement(null, problem);
        }

        var announced = await MaintenanceWindows.AnnounceAsync(context, startsAt, endsAt, now, settings, cancellationToken);

        await AuditTrail.RecordAsync(
            context,
            operatorId,
            AuditAction.MaintenanceAnnounced,
            AuditTarget.Maintenance(announced.Id),
            now,
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserId} announced maintenance {MaintenanceId} from {StartsAt} to {EndsAt}. Announced in advance: {InAdvance}. It acts on no agent",
            operatorId,
            announced.Id,
            announced.StartsAt,
            announced.EndsAt,
            announced.InAdvance);

        return new MaintenanceAnnouncement(announced, null);
    }

    public async Task<bool> CancelAsync(HttpContext request, Guid id)
    {
        if (!await ChangeAsync(request, id, AuditAction.MaintenanceCancelled, MaintenanceWindows.CancelAsync))
        {
            return false;
        }

        logger.LogInformation(
            "User {UserId} cancelled maintenance {MaintenanceId} before it started: no page tells of it any more",
            SignedInUser.IdOf(request),
            id);

        return true;
    }

    public async Task<bool> EndAsync(HttpContext request, Guid id)
    {
        if (!await ChangeAsync(request, id, AuditAction.MaintenanceEnded, MaintenanceWindows.EndAsync))
        {
            return false;
        }

        logger.LogInformation(
            "User {UserId} ended maintenance {MaintenanceId}, which was under way: it is over from now",
            SignedInUser.IdOf(request),
            id);

        return true;
    }

    /// <summary>Cancels or ends what was announced, records it, and says whether this request did it.</summary>
    private async Task<bool> ChangeAsync(
        HttpContext request,
        Guid id,
        AuditAction action,
        Func<EcnanibDbContext, Guid, DateTimeOffset, CancellationToken, Task<bool>> change)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MayAsk(request))
        {
            return false;
        }

        var operatorId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAccountAsync(context, operatorId, cancellationToken))
        {
            return false;
        }

        var now = clock.GetUtcNow();

        if (!await change(context, id, now, cancellationToken))
        {
            // It was as asked for, or is not to be changed any more: the form was sent twice,
            // another operator was first, or it started or ended meanwhile. Nothing was done,
            // so nothing is recorded.
            return false;
        }

        await AuditTrail.RecordAsync(context, operatorId, action, AuditTarget.Maintenance(id), now, AuditOrigin.Of(request), cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>Whether the user of a request is an operator, which everything here is for. It says so in the log when not.</summary>
    private bool MayAsk(HttpContext request)
    {
        if (OperatorAccess.IsOperator(request.User))
        {
            return true;
        }

        logger.LogWarning(
            "User {UserId} was refused announcing, cancelling, or ending maintenance, which is for an operator", SignedInUser.IdOf(request));

        return false;
    }

    /// <summary>
    /// Locks the row of the operator for the rest of the transaction. False when the account
    /// was deleted, on another browser, since the request was signed in.
    /// </summary>
    private static async Task<bool> LockAccountAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var locked = await context.Database
            .SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM users
                WHERE id = {userId}
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

        return locked.Count > 0;
    }
}
