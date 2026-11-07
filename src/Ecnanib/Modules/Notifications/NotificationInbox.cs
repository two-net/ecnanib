using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Notifications;

/// <summary>
/// Every read and change is scoped to the user it's for (NFR-SEC-04). A subject is scrubbed with the
/// logging rules on its way in, as a backstop for FR-NTF-03. See Notifications in docs/architecture.md.
/// </summary>
internal sealed class NotificationInbox(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IAdministrators administrators,
    IEmailVerification email,
    ISecretScrubber scrubber,
    NotificationObservers observers,
    ILogger<NotificationInbox> logger) : INotifications
{
    public int PageSize => 50;

    /// <summary>The live count hears of it when the caller's change commits, and not before, so it never counts a row it can't read.</summary>
    public void Add(EcnanibDbContext context, Notice notice)
    {
        context.Set<Notification>().Add(Row(notice.UserId, notice.Type, notice.Link, notice.Subject, notice.Demo, DateTimeOffset.UtcNow));
        context.AfterCommit(() => observers.Added(notice.UserId));
    }

    public async Task RaiseAsync(Notice notice, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Add(context, notice);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RaiseForAdministratorsAsync(AdministratorNotice notice, CancellationToken cancellationToken)
    {
        var recipients = await administrators.ListAsync(cancellationToken);

        if (recipients.Count == 0)
        {
            logger.LogWarning("No administrator to notify of {NotificationType}", notice.Type);
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var userId in recipients)
        {
            context.Set<Notification>().Add(Row(userId, notice.Type, notice.Link, notice.Subject, demo: false, now));
            context.AfterCommit(() => observers.Added(userId));
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<Notification>().CountAsync(row => row.UserId == userId && row.ReadAt == null, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationRecord>> ListAsync(Guid userId, long? before, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = context.Set<Notification>().AsNoTracking().Where(row => row.UserId == userId);

        if (before is { } cursor)
        {
            rows = rows.Where(row => row.Id < cursor);
        }

        var page = await rows.OrderByDescending(row => row.Id).Take(PageSize).ToListAsync(cancellationToken);

        return [.. page.Select(ToRecord)];
    }

    public async Task MarkReadAsync(Guid userId, long id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Set<Notification>()
            .Where(row => row.Id == id && row.UserId == userId && row.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReadAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task MarkAllReadAsync(Guid userId, long throughId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Set<Notification>()
            .Where(row => row.UserId == userId && row.Id <= throughId && row.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReadAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    private static NotificationRecord ToRecord(Notification row)
    {
        var payload = NotificationPayload.FromJson(row.Payload);

        return new NotificationRecord(row.Id, row.CreatedAt, row.Type, payload.Subject, payload.Demo, row.Link, row.ReadAt);
    }

    private Notification Row(Guid userId, NotificationType type, string link, string? subject, bool demo, DateTimeOffset now) => new()
    {
        UserId = userId,
        Type = type,
        Payload = new NotificationPayload(subject is null ? null : scrubber.Scrub(subject), demo).ToJson(),
        Link = link,
        CreatedAt = now,
        EmailDue = email.IsEnabled,
    };
}
