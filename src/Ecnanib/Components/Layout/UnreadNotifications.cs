namespace Ecnanib.Components.Layout;

/// <summary>
/// Tells the menu's count that the user just read something, which doesn't change the address, so the
/// count wouldn't otherwise know. Scoped, so a circuit has one, and it outlives any layout: a page
/// rendered with a layout of its own, such as Not Found, mounts a new menu, and the page that was left
/// still has to reach it. Live updates from new notifications are #41's.
/// </summary>
public sealed class UnreadNotifications
{
    public event Action? Changed;

    public void Refresh() => Changed?.Invoke();
}
