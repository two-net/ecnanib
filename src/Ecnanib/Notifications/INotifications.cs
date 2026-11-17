namespace Ecnanib.Notifications;

/// <summary>
/// The in-app notifications of users: raising one, and reading the ones that a user has not read
/// (FR-NTF-1). See docs/notifications.md.
/// </summary>
/// <remarks>
/// What is built is what the first event needs: a failure of the protection of a position, which
/// its user is alerted to (FR-RSK-7). The list of all notifications of a user, marking one as
/// read, the other events, and email for critical events are not built.
/// </remarks>
public interface INotifications
{
    /// <summary>Raises a notification for a user.</summary>
    /// <remarks>
    /// It is asked by background work, where nobody is signed in: the user is the owner of the
    /// row that the notification is about, and never something that came in from outside. See
    /// docs/ownership.md.
    /// </remarks>
    /// <param name="userId">The user who is told.</param>
    /// <param name="type">What it is about.</param>
    /// <param name="agentId">The agent that it is about, or null when it is about none.</param>
    /// <param name="content">
    /// What it says, in plain words, in <see cref="Notification.ContentLength"/> characters at
    /// most. It holds no secret.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RaiseAsync(Guid userId, NotificationType type, Guid? agentId, string content, CancellationToken cancellationToken);

    /// <summary>
    /// The notifications about one agent that the signed-in user has not read, newest first.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="agentId">The agent. One of another user has no notifications here.</param>
    Task<IReadOnlyList<NotificationView>> UnreadOfAgentAsync(HttpContext request, Guid agentId);
}

/// <summary>A notification, as its user reads it.</summary>
/// <param name="Id">The id of the notification.</param>
/// <param name="Type">What it is about.</param>
/// <param name="Content">What it says.</param>
/// <param name="CreatedAt">When it was raised.</param>
public sealed record NotificationView(Guid Id, NotificationType Type, string Content, DateTimeOffset CreatedAt);
