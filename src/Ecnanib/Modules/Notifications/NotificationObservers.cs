using Ecnanib.Modules.Notifications.Contracts;

namespace Ecnanib.Modules.Notifications;

/// <summary>
/// Tells every <see cref="INotificationObserver"/> of a new notification, after it has committed. Resolved on first
/// use, because an observer in the Web UI may use Notifications' own contracts, so injecting them would make a cycle
/// at startup.
/// </summary>
internal sealed class NotificationObservers(IServiceProvider services, ILogger<NotificationObservers> logger)
{
    private readonly Lazy<IReadOnlyList<INotificationObserver>> _observers = new(() => [.. services.GetServices<INotificationObserver>()]);

    /// <summary>One observer that throws can't fail the commit's caller, or stop the others.</summary>
    public void Added(Guid userId)
    {
        foreach (var observer in _observers.Value)
        {
            try
            {
                observer.OnAdded(userId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Telling {Observer} about a notification for user {UserId} failed", observer.GetType().Name, userId);
            }
        }
    }
}
