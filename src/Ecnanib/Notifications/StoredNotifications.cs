using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Notifications;

/// <summary>
/// Stores the in-app notifications of users, reads them for their users, and marks them as read
/// (FR-NTF-1). See <see cref="INotifications"/> and docs/notifications.md.
/// </summary>
internal sealed class StoredNotifications(
    IDbContextFactory<EcnanibDbContext> contexts, TimeProvider clock, ILogger<StoredNotifications> logger) : INotifications
{
    /// <summary>The most notifications that the page of an agent shows.</summary>
    public const int Shown = 20;

    public async Task RaiseAsync(
        Guid userId, NotificationType type, Guid? agentId, TradingMode? mode, string content, CancellationToken cancellationToken)
    {
        Check(content);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await AddAsync(context, userId, type, agentId, mode, content, cancellationToken);
    }

    public async Task RaiseInAsync(
        EcnanibDbContext context,
        Guid userId,
        NotificationType type,
        Guid? agentId,
        TradingMode? mode,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Check(content);

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A notification is raised inside the transaction of what it tells of, and this context holds none.");
        }

        // Written inside the transaction of whoever asked, and stands for a row that is stored with it.
        await AddAsync(context, userId, type, agentId, mode, content, cancellationToken);
    }

    public async Task<bool> RaiseOnceAsync(
        Guid userId,
        NotificationType type,
        Guid agentId,
        TradingMode mode,
        string content,
        TimeSpan within,
        CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - within;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // Asked for by owner. Two that ask at the same moment may both raise one: it is
            // said twice then, which loses nothing. What was said of the agent in the other
            // mode is not the same thing said again: an agent that was switched is told anew.
            if (await context.Set<Notification>()
                .AsNoTracking()
                .OwnedBy(userId)
                .AnyAsync(
                    notification => notification.AgentId == agentId
                        && notification.Type == type
                        && notification.Mode == mode
                        && notification.CreatedAt > since,
                    cancellationToken))
            {
                return false;
            }
        }

        await RaiseAsync(userId, type, agentId, mode, content, cancellationToken);

        return true;
    }

    public async Task<IReadOnlyList<NotificationView>> UnreadOfAgentAsync(HttpContext request, Guid agentId)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: the notifications about an agent of another user are not found.
        return await Views(
                context.Set<Notification>()
                    .AsNoTracking()
                    .OwnedBy(userId)
                    .Where(notification => notification.AgentId == agentId && !notification.IsRead)
                    .OrderByDescending(notification => notification.CreatedAt)
                    .ThenByDescending(notification => notification.Id)
                    .Take(Shown))
            .ToListAsync(cancellationToken);
    }

    public async Task<NotificationList> OfUserAsync(HttpContext request, int page)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner, each of them: the notifications of another user are not counted
        // and not found.
        var owned = context.Set<Notification>().AsNoTracking().OwnedBy(userId);

        var total = await owned.CountAsync(cancellationToken);
        var unread = await owned.CountAsync(notification => !notification.IsRead, cancellationToken);

        var newestFirst = owned.OrderByDescending(notification => notification.CreatedAt).ThenByDescending(notification => notification.Id);

        var latest = await newestFirst.Select(notification => (Guid?)notification.Id).FirstOrDefaultAsync(cancellationToken);

        var notifications = await Views(newestFirst.Skip((page - 1) * NotificationList.PageSize).Take(NotificationList.PageSize))
            .ToListAsync(cancellationToken);

        // The first page is always there, also for a user who has been told nothing.
        var pages = Math.Max((total + NotificationList.PageSize - 1) / NotificationList.PageSize, 1);

        return new NotificationList(notifications, page, pages, total, unread, latest);
    }

    public async Task<UnreadByMode> UnreadByModeAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The counts and the lists as they were at one moment: a notification that is raised
        // or read meanwhile is in all of the answer, or in none of it.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by owner, each of them: what another user has not read is not counted and
        // not found. A user has few that they have not read, which an index of their own holds.
        var unread = context.Set<Notification>().AsNoTracking().OwnedBy(userId).Where(notification => !notification.IsRead);

        var totals = await unread
            .GroupBy(notification => notification.Mode)
            .Select(group => new { Mode = group.Key, Total = group.Count() })
            .ToListAsync(cancellationToken);

        async Task<UnreadOfMode> OfAsync(TradingMode? mode)
        {
            var total = totals.SingleOrDefault(counted => counted.Mode == mode)?.Total ?? 0;

            if (total == 0)
            {
                return new UnreadOfMode([], 0);
            }

            return new UnreadOfMode(
                await Views(
                        unread
                            .Where(notification => notification.Mode == mode)
                            .OrderByDescending(notification => notification.CreatedAt)
                            .ThenByDescending(notification => notification.Id)
                            .Take(UnreadByMode.Shown))
                    .ToListAsync(cancellationToken),
                total);
        }

        // Each mode is asked for on its own, and none of them through the other (FR-BIN-11).
        return new UnreadByMode(await OfAsync(TradingMode.Live), await OfAsync(TradingMode.Demo), await OfAsync(null));
    }

    public async Task<int> UnreadCountAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);

        await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);

        return await context.Set<Notification>()
            .AsNoTracking()
            .OwnedBy(userId)
            .CountAsync(notification => !notification.IsRead, request.RequestAborted);
    }

    public async Task MarkReadAsync(HttpContext request, Guid notificationId)
    {
        var userId = SignedInUser.RequiredIdOf(request);

        await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);

        // By owner: the notification of another user is not found, and stays as it is. One that
        // is read already is not written again.
        await context.Set<Notification>()
            .OwnedBy(userId)
            .Where(notification => notification.Id == notificationId && !notification.IsRead)
            .ExecuteUpdateAsync(row => row.SetProperty(notification => notification.IsRead, true), request.RequestAborted);
    }

    public async Task<int> MarkAllReadAsync(HttpContext request, Guid through)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // By owner: the notification of another user names no time here, and nothing is marked.
        var shown = await context.Set<Notification>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(notification => notification.Id == through)
            .Select(notification => (DateTimeOffset?)notification.CreatedAt)
            .SingleOrDefaultAsync(cancellationToken);

        if (shown is not { } until)
        {
            return 0;
        }

        // What was raised by then, which the page showed, or had on a later page of it. One
        // that was raised since is not read, and stays so.
        return await context.Set<Notification>()
            .OwnedBy(userId)
            .Where(notification => !notification.IsRead && notification.CreatedAt <= until)
            .ExecuteUpdateAsync(row => row.SetProperty(notification => notification.IsRead, true), cancellationToken);
    }

    private static void Check(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(content.Length, Notification.ContentLength, nameof(content));
    }

    /// <summary>Writes the row of a notification with a context, and saves the context.</summary>
    private async Task AddAsync(
        EcnanibDbContext context,
        Guid userId,
        NotificationType type,
        Guid? agentId,
        TradingMode? mode,
        string content,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // An email is to be sent for a critical event, to a user who has opted in to it by now
        // (FR-NTF-3). What sends it asks once more, as it sends: see NotificationEmails.
        var emailPending = NotificationTypes.IsCritical(type)
            && await context.Set<EmailOptIn>().AsNoTracking().OwnedBy(userId).AnyAsync(cancellationToken);

        context.Set<Notification>().Add(
            new Notification(Guid.CreateVersion7(now), userId, type, agentId, mode, content, isRead: false, now, emailPending, emailedAt: null));

        await context.SaveChangesAsync(cancellationToken);

        // What it says stays in its row: the log names it by what it is about.
        logger.LogInformation(
            "A notification was raised for user {UserId} about agent {AgentId}: {NotificationType}", userId, agentId, type);
    }

    private static IQueryable<NotificationView> Views(IQueryable<Notification> notifications) =>
        notifications.Select(notification => new NotificationView(
            notification.Id,
            notification.Type,
            notification.AgentId,
            notification.Content,
            notification.IsRead,
            notification.CreatedAt,
            notification.Mode));
}

/// <summary>A notification of a user, as the export of their data holds it.</summary>
/// <param name="Type">What it is about.</param>
/// <param name="AgentId">The agent that it is about, or null when it is about none.</param>
/// <param name="Mode">The mode that it is about, or null when it is about none.</param>
/// <param name="Content">What it says.</param>
/// <param name="IsRead">Whether its user has read it.</param>
/// <param name="CreatedAt">When it was raised.</param>
/// <param name="EmailedAt">When it was sent to the user by email as well, or null when it was not.</param>
public sealed record ExportedNotification(
    NotificationType Type,
    Guid? AgentId,
    TradingMode? Mode,
    string Content,
    bool IsRead,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EmailedAt);

/// <summary>The notifications of a user, for the export of their data (NFR-SEC-9, FR-NTF-1).</summary>
internal sealed class NotificationsExport : IExportedPart
{
    public string Section => "notifications";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        // Every notification, in the order they were raised in.
        var notifications = await context.Set<Notification>()
            .AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(notification => notification.CreatedAt)
            .ThenBy(notification => notification.Id)
            .ToListAsync(cancellationToken);

        return
        [
            .. notifications.Select(notification => new ExportedNotification(
                notification.Type,
                notification.AgentId,
                notification.Mode,
                notification.Content,
                notification.IsRead,
                notification.CreatedAt,
                notification.EmailedAt)),
        ];
    }
}
