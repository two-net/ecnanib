using Ecnanib.Notifications;
using Ecnanib.Tests.Accounts;

namespace Ecnanib.Tests.Notifications;

/// <summary>Notifications that a test needs a user to have, without what raises them.</summary>
internal static class StoredNotificationRows
{
    /// <summary>What the notification of <see cref="GiveNotificationAsync"/> says.</summary>
    public const string Content = "The stop-loss of the position in BTCUSDT (Demo) could not be placed, so the position was closed at market.";

    /// <summary>
    /// Gives a user a notification about one of their agents, which they have not read, stored
    /// as the application stores one. It is for a test that is about something else, and needs
    /// a row in every table that holds rows of users.
    /// </summary>
    /// <returns>The id of the notification.</returns>
    public static async Task<Guid> GiveNotificationAsync(
        this AccountsApplication application, Guid userId, Guid agentId, DateTimeOffset at, string content = Content)
    {
        var id = Guid.CreateVersion7();

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Add(new Notification(id, userId, NotificationType.ProtectionFailure, agentId, content, isRead: false, at));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }
}
