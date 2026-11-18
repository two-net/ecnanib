using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Notifications;

/// <summary>
/// Stores the in-app notifications of users, and reads them for their users (FR-NTF-1). See
/// <see cref="INotifications"/> and docs/notifications.md.
/// </summary>
internal sealed class StoredNotifications(
    IDbContextFactory<EcnanibDbContext> contexts, TimeProvider clock, ILogger<StoredNotifications> logger) : INotifications
{
    /// <summary>The most notifications that the page of an agent shows.</summary>
    public const int Shown = 20;

    public async Task RaiseAsync(
        Guid userId, NotificationType type, Guid? agentId, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(content.Length, Notification.ContentLength, nameof(content));

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        context.Set<Notification>().Add(
            new Notification(Guid.CreateVersion7(clock.GetUtcNow()), userId, type, agentId, content, isRead: false, clock.GetUtcNow()));

        await context.SaveChangesAsync(cancellationToken);

        // What it says stays in its row: the log names it by what it is about.
        logger.LogInformation(
            "A notification was raised for user {UserId} about agent {AgentId}: {NotificationType}", userId, agentId, type);
    }

    public async Task<bool> RaiseOnceAsync(
        Guid userId, NotificationType type, Guid agentId, string content, TimeSpan within, CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - within;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // Asked for by owner. Two that ask at the same moment may both raise one: it is
            // said twice then, which loses nothing.
            if (await context.Set<Notification>()
                .AsNoTracking()
                .OwnedBy(userId)
                .AnyAsync(
                    notification => notification.AgentId == agentId && notification.Type == type && notification.CreatedAt > since,
                    cancellationToken))
            {
                return false;
            }
        }

        await RaiseAsync(userId, type, agentId, content, cancellationToken);

        return true;
    }

    public async Task<IReadOnlyList<NotificationView>> UnreadOfAgentAsync(HttpContext request, Guid agentId)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: the notifications about an agent of another user are not found.
        return await context.Set<Notification>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(notification => notification.AgentId == agentId && !notification.IsRead)
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(Shown)
            .Select(notification => new NotificationView(notification.Id, notification.Type, notification.Content, notification.CreatedAt))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>A notification of a user, as the export of their data holds it.</summary>
/// <param name="Type">What it is about.</param>
/// <param name="AgentId">The agent that it is about, or null when it is about none.</param>
/// <param name="Content">What it says.</param>
/// <param name="IsRead">Whether its user has read it.</param>
/// <param name="CreatedAt">When it was raised.</param>
public sealed record ExportedNotification(NotificationType Type, Guid? AgentId, string Content, bool IsRead, DateTimeOffset CreatedAt);

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
                notification.Type, notification.AgentId, notification.Content, notification.IsRead, notification.CreatedAt)),
        ];
    }
}
