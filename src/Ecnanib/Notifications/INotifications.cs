using Ecnanib.Platform.Persistence;

namespace Ecnanib.Notifications;

/// <summary>
/// The in-app notifications of users: raising one, reading them, and marking them as read
/// (FR-NTF-1). See docs/notifications.md.
/// </summary>
/// <remarks>
/// A notification is raised for: a position that was opened or closed, an agent that was halted
/// or waits for its user, a key that is invalid, a model that is unavailable, a failure of an
/// order or of the protection of a position, records that did not match the exchange, and a
/// position that approaches liquidation. And for an entry that was skipped, a role that was
/// given another model, and an agent that runs again after a halt. What raises each is in
/// docs/notifications.md. A notification about a critical event is sent by email as well, to a
/// user who opted in: see <see cref="IEmailOptIn"/>.
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
    /// Raises a notification for a user inside the transaction of whoever asks: it is stored
    /// when what it tells of is stored, and not when that is not.
    /// </summary>
    /// <remarks>
    /// It is for what is told inside the transaction that changes a key, a model, or the state
    /// of an agent, and that is not stored when the transaction is not. It saves the context,
    /// and whoever asks commits.
    /// </remarks>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user who is told.</param>
    /// <param name="type">What it is about.</param>
    /// <param name="agentId">The agent that it is about, or null when it is about none.</param>
    /// <param name="content">What it says, as for <see cref="RaiseAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The context holds no transaction. A notification that is written on its own would stand
    /// when what it tells of fails.
    /// </exception>
    Task RaiseInAsync(
        EcnanibDbContext context, Guid userId, NotificationType type, Guid? agentId, string content, CancellationToken cancellationToken);

    /// <summary>
    /// Raises a notification about an agent, unless one of the same type about the same agent
    /// was raised within a time: for what happens again and again while nothing changes, and is
    /// worth saying once in that time.
    /// </summary>
    /// <param name="userId">The user who is told.</param>
    /// <param name="type">What it is about.</param>
    /// <param name="agentId">The agent that it is about.</param>
    /// <param name="content">What it says, as for <see cref="RaiseAsync"/>.</param>
    /// <param name="within">The time in which it is said once.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was raised. It was not when one was raised within the time.</returns>
    Task<bool> RaiseOnceAsync(
        Guid userId, NotificationType type, Guid agentId, string content, TimeSpan within, CancellationToken cancellationToken);

    /// <summary>
    /// The notifications about one agent that the signed-in user has not read, newest first.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="agentId">The agent. One of another user has no notifications here.</param>
    Task<IReadOnlyList<NotificationView>> UnreadOfAgentAsync(HttpContext request, Guid agentId);

    /// <summary>
    /// The notifications of the signed-in user, read and not read, newest first, one page of
    /// them.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="page">Which page, from 1. A page after the last one holds none.</param>
    Task<NotificationList> OfUserAsync(HttpContext request, int page);

    /// <summary>How many notifications the signed-in user has not read.</summary>
    /// <param name="request">The request, which says who is signed in.</param>
    Task<int> UnreadCountAsync(HttpContext request);

    /// <summary>
    /// Marks a notification of the signed-in user as read. One that is read already stays so,
    /// and one of another user is not found, and stays as it is.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="notificationId">The notification.</param>
    Task MarkReadAsync(HttpContext request, Guid notificationId);

    /// <summary>
    /// Marks the notifications of the signed-in user as read that were raised no later than one
    /// of them: the newest that the page showed. So one that was raised after the page was
    /// written is not marked as read by a user who has not seen it.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="through">The newest notification that the user was shown. With one that is not theirs, nothing is marked.</param>
    /// <returns>How many were marked.</returns>
    Task<int> MarkAllReadAsync(HttpContext request, Guid through);
}

/// <summary>A notification, as its user reads it.</summary>
/// <param name="Id">The id of the notification.</param>
/// <param name="Type">What it is about.</param>
/// <param name="AgentId">The agent that it is about, or null when it is about none.</param>
/// <param name="Content">What it says.</param>
/// <param name="IsRead">Whether its user has read it.</param>
/// <param name="CreatedAt">When it was raised.</param>
public sealed record NotificationView(
    Guid Id, NotificationType Type, Guid? AgentId, string Content, bool IsRead, DateTimeOffset CreatedAt);

/// <summary>One page of the notifications of a user, newest first.</summary>
/// <param name="Notifications">The notifications of the page.</param>
/// <param name="Page">Which page it is, from 1.</param>
/// <param name="Pages">How many pages there are: 1 at the least.</param>
/// <param name="Total">How many notifications the user has in all.</param>
/// <param name="Unread">How many of them the user has not read.</param>
/// <param name="Latest">The newest notification of the user, or null when there is none.</param>
public sealed record NotificationList(
    IReadOnlyList<NotificationView> Notifications, int Page, int Pages, int Total, int Unread, Guid? Latest)
{
    /// <summary>How many notifications a page holds.</summary>
    public const int PageSize = 25;
}
