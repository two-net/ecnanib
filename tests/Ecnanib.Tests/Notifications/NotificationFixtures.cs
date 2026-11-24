using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Notifications;

/// <summary>Notifications that a test needs a user to have, without what raises them.</summary>
internal static class StoredNotificationRows
{
    /// <summary>What the notification of <see cref="GiveNotificationAsync"/> says.</summary>
    public const string Content = "The stop-loss of the position in BTCUSDT (Demo) could not be placed, so the position was closed at market.";

    /// <summary>
    /// Gives a user a notification about one of their agents, which they have not read, stored
    /// as the application stores one, with no email to be sent for it. It is for a test that is
    /// about something else, and needs a row in every table that holds rows of users, and for a
    /// test of the pages, which says when each was raised, and the mode that each is about.
    /// </summary>
    /// <returns>The id of the notification.</returns>
    public static async Task<Guid> GiveNotificationAsync(
        this AccountsApplication application,
        Guid userId,
        Guid? agentId,
        DateTimeOffset at,
        string content = Content,
        NotificationType type = NotificationType.ProtectionFailure,
        TradingMode? mode = null)
    {
        // An id of the time that the notification is of, as the application assigns one.
        var id = Guid.CreateVersion7(at);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Add(new Notification(id, userId, type, agentId, mode, content, isRead: false, at, emailPending: false, emailedAt: null));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    /// <summary>
    /// What a user was told: what each notification is about, the agent that it is about, and
    /// what it says. In the order that they were raised in, and those of one moment by their
    /// agent and by what they say: the ids of one moment are in no order of their own.
    /// </summary>
    public static async Task<IReadOnlyList<(NotificationType Type, Guid? AgentId, string Content)>> ToldAsync(
        this AccountsApplication application, Guid userId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var stored = await context.Set<Notification>()
            .AsNoTracking()
            .Where(notification => notification.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);

        return
        [
            .. stored
                .OrderBy(notification => notification.CreatedAt)
                .ThenBy(notification => notification.AgentId)
                .ThenBy(notification => notification.Content, StringComparer.Ordinal)
                .Select(notification => (notification.Type, notification.AgentId, notification.Content)),
        ];
    }

    /// <summary>
    /// Gives a user the row that says they opted in to email for critical events, as the page
    /// stores it. It is for a test that is about something else, and needs a row in every table
    /// that holds rows of users.
    /// </summary>
    public static async Task GiveEmailOptInAsync(this AccountsApplication application, Guid userId, DateTimeOffset at)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Add(new EmailOptIn(userId, at));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
