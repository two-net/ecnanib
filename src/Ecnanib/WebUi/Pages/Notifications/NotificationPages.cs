using System.Globalization;
using Ecnanib.Notifications;

namespace Ecnanib.WebUi.Pages.Notifications;

/// <summary>
/// The texts of the pages of the notifications, in one place (NFR-USA-4), the names of their
/// forms, and how the address of the page of the notifications is read. What each kind of
/// notification is called is in <see cref="NotificationTypes"/>, because an email says it too.
/// See "The pages" in docs/notifications.md.
/// </summary>
public static class NotificationPages
{
    /// <summary>The heading of the page of the notifications, and the text of the link to it.</summary>
    public const string Title = "Notifications";

    /// <summary>The heading of the page a user opts in to email for critical events on.</summary>
    public const string EmailTitle = "Email for critical events";

    /// <summary>The text of the link that leads from a page of an agent to every notification of its user.</summary>
    public const string AllLink = "All your notifications";

    /// <summary>The text of the button that marks a notification as read.</summary>
    public const string MarkRead = "Mark as read";

    /// <summary>The text of the button that marks every notification as read.</summary>
    public const string MarkAll = "Mark all as read";

    /// <summary>The name of the form that marks every notification as read.</summary>
    public const string MarkAllForm = "mark-all-read";

    /// <summary>The name of the field of that form which says the newest notification that the page showed.</summary>
    public const string ThroughField = "Through";

    /// <summary>The name of the form that opts a user in to email for critical events.</summary>
    public const string EmailOnForm = "email-on";

    /// <summary>The name of the form that opts a user out of it.</summary>
    public const string EmailOffForm = "email-off";

    /// <summary>The text of the button that opts a user in to email for critical events.</summary>
    public const string EmailOn = "Send me email for critical events";

    /// <summary>The text of the button that opts a user out of it.</summary>
    public const string EmailOff = "Stop the email for critical events";

    /// <summary>What the page of the notifications is (FR-NTF-1).</summary>
    public const string Explained =
        "What the service has to tell you: a position that was opened or closed, an agent that was halted or waits for you, " +
        "a key that is invalid, a model that is unavailable, an order or a protection that failed, records that did not match " +
        "Binance, and a position that approaches liquidation. A notification stays here once you have read it.";

    /// <summary>What the page says to a user who has been told nothing.</summary>
    public const string None = "The service has told you nothing yet.";

    /// <summary>What a notification that its user has not read says of itself.</summary>
    public const string NotRead = "Not read.";

    /// <summary>What a notification that its user has read says of itself.</summary>
    public const string Read = "Read.";

    /// <summary>What email for critical events is, and that it is the user's own choice (FR-NTF-3).</summary>
    public const string EmailExplained =
        "The service tells you what matters on the page of your notifications. You can have the critical events sent to you by " +
        "email as well: those that stop an agent from trading, or that put money at risk. No email of this kind is sent unless " +
        "you choose so here, and you can turn it off again at any time.";

    /// <summary>What an email holds, as the page that turns it on says.</summary>
    public const string EmailHolds =
        "An email says what the notification says, with the symbol and the mode of the agent. It holds no key and no password. " +
        "Events that happen close together are sent in one message.";

    /// <summary>What the page says to a user who has not opted in.</summary>
    public const string EmailIsOff = "Off. You are sent no email for critical events.";

    /// <summary>What the page says to a user whose address is not verified: no email goes to it.</summary>
    public const string AddressNotVerified =
        "Your email address is not verified yet, and no email of this kind is sent to an address that is not. " +
        "Open the link in the message that was sent to you when you registered.";

    /// <summary>The text of the link to the page of the notifications in the bar at the top of every page, with how many are not read.</summary>
    public static string Link(int unread) => unread > 0 ? $"{Title} ({Count(unread)})" : Title;

    /// <summary>The name of the form that marks one notification as read: each has its own.</summary>
    public static string MarkReadForm(Guid notificationId) => $"mark-read-{notificationId}";

    /// <summary>
    /// The id of the element that says what a notification says, which the button that marks it
    /// as read is described by: every such button says the same, and this says which it is for.
    /// </summary>
    public static string TextId(Guid notificationId) => $"notification-{notificationId}";

    /// <summary>How many notifications a user has, and how many of them are not read.</summary>
    public static string Counted(NotificationList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var all = list.Total == 1 ? "1 notification" : $"{Count(list.Total)} notifications";

        return list.Unread == 0 ? $"{all}, all read" : $"{all}, {Count(list.Unread)} not read";
    }

    /// <summary>Which page of the notifications a page is.</summary>
    public static string PageOf(NotificationList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return $"Page {Count(list.Page)} of {Count(list.Pages)}";
    }

    /// <summary>What the page says to a user who opted in: that it is on, and where an email goes.</summary>
    public static string EmailIsOn(string? address) =>
        address is null ? "On." : $"On. Critical events are sent to {address}.";

    /// <summary>
    /// Which page of the notifications an address asks for. Null for an address that names a
    /// page which there is not: no whole number from 1, or a page twice. A page that is absent
    /// or empty is the first. What the address names besides is not gone by.
    /// </summary>
    public static int? PageOf(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var number = query[WebUiModule.NotificationsPageKey];

        // A choice that is made twice is none that there is.
        if (number.Count > 1)
        {
            return null;
        }

        // A choice that is absent or empty is not made.
        if (number is not [{ Length: > 0 } written])
        {
            return 1;
        }

        // As the page itself writes a number, and in no other form of one.
        return int.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            && page >= 1
            && page.ToString(CultureInfo.InvariantCulture) == written
                ? page
                : null;
    }

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
