using System.Globalization;
using System.Text.Json.Serialization;
using Ecnanib.WebUi.Pages;

namespace Ecnanib.WebUi.Layout;

/// <summary>
/// The texts of the notice before a session ends, in one place (NFR-USA-1, NFR-USA-4): what the
/// dialog of <c>SessionNotice</c> says, and what the page is answered when it asks how long is
/// left. See "The notice before a session ends" in docs/accounts.md.
/// </summary>
public static class SessionPages
{
    /// <summary>The name of the dialog, which is what a screen reader says first.</summary>
    public const string Title = "You are about to be signed out";

    /// <summary>
    /// What the dialog says as the page is written, without a time. The page puts
    /// <see cref="At"/> in its place before it shows the dialog: this stays where the server
    /// could not be asked.
    /// </summary>
    public const string Soon =
        "Nothing was done here for a while. To keep your account safe, you will be signed out soon, and this page will then ask you to sign in again.";

    /// <summary>The button that says its user is still there: the session lasts its whole period again.</summary>
    public const string Stay = "Stay signed in";

    /// <summary>The button that signs out at once. It is the button of the bar, said again here.</summary>
    public const string SignOutNow = "Sign out now";

    /// <summary>What the dialog adds when the server gave no answer: nothing is said of a session that is not known.</summary>
    public const string NotReached =
        "The server could not be reached, so it is not known whether you are still signed in. Check your connection, and try again.";

    /// <summary>
    /// What the dialog says: why, how long is left, and the time of day that the session ends
    /// at. The time is said once and does not count down, so a screen reader reads it once.
    /// </summary>
    /// <param name="left">How long the session has left.</param>
    /// <param name="at">The moment that it ends.</param>
    public static string At(TimeSpan left, DateTimeOffset at) =>
        $"Nothing was done here for a while. To keep your account safe, you will be signed out {In(left)}, at {PageTimes.TimeOfDay(at)}, and this page will then ask you to sign in again.";

    /// <summary>How long is left, in whole minutes as a person says them: <c>in about 2 minutes</c>.</summary>
    private static string In(TimeSpan left)
    {
        var minutes = (int)Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero);

        return minutes switch
        {
            < 1 => "in less than a minute",
            1 => "in about a minute",
            _ => $"in about {minutes.ToString(CultureInfo.InvariantCulture)} minutes",
        };
    }
}

/// <summary>
/// What a page is answered when it asks how long its session has left, and when its user said
/// that they are still there.
/// </summary>
/// <param name="Left">The whole seconds that are left. Nothing for a visitor, and for a session that has ended.</param>
/// <param name="Says">What the notice says of it, where something is left: <see cref="SessionPages.At"/>.</param>
public sealed record SessionLeft(
    int Left,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Says);
