namespace Ecnanib.Modules.Notifications.Contracts;

/// <summary>
/// Hears that a user has a new notification, once it has committed, so the menu's count and an open list update
/// without a reload (FR-UI-05). The Web UI subscribes, and reads again through <see cref="INotifications"/>. Register
/// an implementation as a singleton. It's an in-process event, lost on a crash, so a page reads when it opens. See
/// Live updates in docs/architecture.md.
/// </summary>
public interface INotificationObserver
{
    /// <summary>
    /// Called on the thread that committed it, so it must return at once and never throw. One that throws is logged,
    /// and stops neither the commit's caller nor the other observers. It hears every user, so it filters by user.
    /// </summary>
    void OnAdded(Guid userId);
}
