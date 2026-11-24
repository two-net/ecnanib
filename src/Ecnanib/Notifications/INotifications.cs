using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;

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
/// <para>
/// <b>Whoever raises a notification says the mode that it is about:</b> the mode of the agent,
/// of the position, or of the Binance key, as it is then, and none for what is about no mode. It
/// is stored with the notification, so what happened in Demo is never listed under Live
/// (FR-BIN-11): the dashboard lists what a user has not read for each mode (FR-RPT-1).
/// </para>
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
    /// <param name="mode">The mode that it is about, or null when it is about none.</param>
    /// <param name="content">
    /// What it says, in plain words, in <see cref="Notification.ContentLength"/> characters at
    /// most. It holds no secret.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RaiseAsync(
        Guid userId, NotificationType type, Guid? agentId, TradingMode? mode, string content, CancellationToken cancellationToken);

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
    /// <param name="mode">The mode that it is about, or null when it is about none.</param>
    /// <param name="content">What it says, as for <see cref="RaiseAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The context holds no transaction. A notification that is written on its own would stand
    /// when what it tells of fails.
    /// </exception>
    Task RaiseInAsync(
        EcnanibDbContext context,
        Guid userId,
        NotificationType type,
        Guid? agentId,
        TradingMode? mode,
        string content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Raises a notification about an agent, unless one of the same type about the same agent
    /// in the same mode was raised within a time: for what happens again and again while nothing
    /// changes, and is worth saying once in that time.
    /// </summary>
    /// <remarks>
    /// What was said of an agent in Demo does not stand for what happens to it in Live: an
    /// agent that was switched is told of the first time in its new mode, whatever was said in
    /// the mode before.
    /// </remarks>
    /// <param name="userId">The user who is told.</param>
    /// <param name="type">What it is about.</param>
    /// <param name="agentId">The agent that it is about.</param>
    /// <param name="mode">The mode that it is about, which is that of the agent, or of its position.</param>
    /// <param name="content">What it says, as for <see cref="RaiseAsync"/>.</param>
    /// <param name="within">The time in which it is said once.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was raised. It was not when one was raised within the time.</returns>
    Task<bool> RaiseOnceAsync(
        Guid userId,
        NotificationType type,
        Guid agentId,
        TradingMode mode,
        string content,
        TimeSpan within,
        CancellationToken cancellationToken);

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

    /// <summary>
    /// The notifications that the signed-in user has not read, by the mode that each is about:
    /// those of Live, those of Demo, and those that are about no mode, each newest first, and
    /// each with how many there are. The dashboard lists them as its alerts, for each mode on its
    /// own (FR-RPT-1, FR-BIN-11).
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    Task<UnreadByMode> UnreadByModeAsync(HttpContext request);

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
/// <param name="Mode">The mode that it is about, as it was said when it was raised, or null when it is about none.</param>
public sealed record NotificationView(
    Guid Id, NotificationType Type, Guid? AgentId, string Content, bool IsRead, DateTimeOffset CreatedAt, TradingMode? Mode = null);

/// <summary>
/// The notifications that a user has not read, by the mode that each is about. What is about
/// one mode is never listed with what is about the other (FR-BIN-11).
/// </summary>
/// <param name="Live">Those that are about Live.</param>
/// <param name="Demo">Those that are about Demo.</param>
/// <param name="OfNoMode">
/// Those that are about no mode: a key of an AI provider, and one that was raised before
/// notifications said their mode, about an agent that is deleted since.
/// </param>
public sealed record UnreadByMode(UnreadOfMode Live, UnreadOfMode Demo, UnreadOfMode OfNoMode)
{
    /// <summary>The most notifications that are listed for a mode.</summary>
    public const int Shown = 10;

    /// <summary>Those that are about a mode.</summary>
    public UnreadOfMode Of(TradingMode mode) => mode == TradingMode.Live ? Live : Demo;
}

/// <summary>The notifications about one mode that a user has not read.</summary>
/// <param name="Latest">The newest of them, newest first: <see cref="UnreadByMode.Shown"/> at the most.</param>
/// <param name="Total">How many there are in all.</param>
public sealed record UnreadOfMode(IReadOnlyList<NotificationView> Latest, int Total);

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
