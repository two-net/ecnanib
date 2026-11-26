using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the two pages of the appearance, in one place (NFR-USA-4): the one among the
/// settings, where a user chooses the theme of their account, and the one of a visitor, who
/// chooses the theme of their device (FR-THM-1, FR-THM-3, FR-UI-2). See docs/themes.md. The page
/// of a user also takes how much of the pages moves (FR-ANI-4), see docs/motion.md, and the time
/// zone that the pages write a time in (NFR-USA-3), see docs/times.md.
/// </summary>
public static class AppearancePages
{
    /// <summary>The heading of the page of a visitor. The page of a user is named by its screen.</summary>
    public static string Title => "Appearance";

    /// <summary>The name of the form that saves the choice, on either page.</summary>
    public const string Form = "theme";

    /// <summary>The name of the field of that form that says which theme was chosen.</summary>
    public const string Field = "Theme";

    /// <summary>What the choices are a choice of.</summary>
    public static string Legend => "Theme";

    /// <summary>The button that saves the theme. Each of the two buttons of the page says what it saves (NFR-USA-1).</summary>
    public static string Save => "Save the theme";

    /// <summary>The button that saves how much of the pages moves.</summary>
    public static string SaveMotion => "Save the animation";

    /// <summary>What a form is told that names no theme.</summary>
    public static string ChooseOne => "Choose System, Dark, or Light.";

    /// <summary>What a user is told about where their choice holds.</summary>
    public static string SavedToAccount =>
        "Your choice is saved to your account, and the pages look that way on every device you sign in on.";

    /// <summary>What a visitor is told about where their choice holds.</summary>
    public static string SavedOnDevice =>
        "Your choice is saved on this device, in this browser. Sign in to save a choice to your account, " +
        "which the pages then follow on every device you sign in on.";

    /// <summary>The themes, in the order that a page offers them: the one of a new user first.</summary>
    public static readonly IReadOnlyList<Theme> Choices = [Theme.System, Theme.Dark, Theme.Light];

    /// <summary>What a theme is called.</summary>
    public static string NameOf(Theme theme) => theme switch
    {
        Theme.Dark => "Dark",
        Theme.Light => "Light",
        _ => "System",
    };

    /// <summary>What choosing a theme means.</summary>
    public static string Explain(Theme theme) => theme switch
    {
        Theme.Dark => "The pages are dark, whatever your device is set to.",
        Theme.Light => "The pages are light, whatever your device is set to.",
        _ => "The pages are light or dark as your device is set, and change when its setting does.",
    };

    /// <summary>What the page of the account says of the theme that is stored with it.</summary>
    public static string Stored(Theme theme) =>
        $"Theme: {NameOf(theme)}. Your choice is stored with your account, and is in a download.";

    /// <summary>The name of the form that saves how much of the pages moves.</summary>
    public const string MotionForm = "motion";

    /// <summary>The name of the field of that form that says what was chosen.</summary>
    public const string MotionField = "Motion";

    /// <summary>What those choices are a choice of.</summary>
    public static string MotionLegend => "Animation";

    /// <summary>What a form is told that names no such choice.</summary>
    public static string ChooseMotion => "Choose System or Reduced.";

    /// <summary>The choices, in the order that the page offers them: the one of a new user first.</summary>
    public static readonly IReadOnlyList<Motion> MotionChoices = [Motion.System, Motion.Reduced];

    /// <summary>What a choice of how much moves is called.</summary>
    public static string NameOf(Motion motion) => motion switch
    {
        Motion.Reduced => "Reduced",
        _ => "System",
    };

    /// <summary>What choosing it means.</summary>
    public static string Explain(Motion motion) => motion switch
    {
        Motion.Reduced => "A change shows at once and nothing moves by itself, whatever your device asks for.",
        _ => "The pages move as they change, unless your device asks for reduced motion.",
    };

    /// <summary>What the page of the account says of the choice that is stored with it.</summary>
    public static string Stored(Motion motion) =>
        $"Animation: {NameOf(motion)}. Your choice is stored with your account, and is in a download.";

    /// <summary>The name of the form that saves the time zone that the pages write a time in.</summary>
    public const string TimeForm = "times";

    /// <summary>The name of the field of that form that says what was chosen.</summary>
    public const string TimeField = "Times";

    /// <summary>What those choices are a choice of.</summary>
    public static string TimeLegend => "Time zone";

    /// <summary>The button that saves the time zone.</summary>
    public static string SaveTime => "Save the time zone";

    /// <summary>What a form is told that names no such choice.</summary>
    public static string ChooseTime => "Choose This device or UTC.";

    /// <summary>The choices, in the order that the page offers them: the one of a new user first.</summary>
    public static readonly IReadOnlyList<TimeDisplay> TimeChoices = [TimeDisplay.Device, TimeDisplay.Utc];

    /// <summary>What a choice of the time zone is called.</summary>
    public static string NameOf(TimeDisplay display) => display switch
    {
        TimeDisplay.Utc => "UTC",
        _ => "This device",
    };

    /// <summary>What choosing it means.</summary>
    public static string Explain(TimeDisplay display) => display switch
    {
        TimeDisplay.Utc =>
            "Times are written in UTC, whatever your device says: the time that Binance and the candles go by.",
        _ =>
            "Times are written in the time zone of the device that you sign in on, and each says how far that is from UTC. " +
            "A device says its time zone when you sign in on it.",
    };

    /// <summary>
    /// What the page says of the device that it is opened on, under the choices: the time zone
    /// that the device has said, by its name, with how far that is from UTC now.
    /// </summary>
    /// <param name="zone">The name of the time zone that the device has said.</param>
    /// <param name="offset">How far it is from UTC now, as a time says it.</param>
    public static string DeviceSays(string zone, string offset) =>
        $"This device has said that its time zone is {zone}, which is {offset} now.";

    /// <summary>
    /// What it says of a device that has said no time zone: the pages are in UTC there, also
    /// for a user who chose the device, and how the device comes to say one.
    /// </summary>
    public static string DeviceSaysNone =>
        "This device has said no time zone yet, so its times are in UTC. It says one when you save this choice here, or " +
        "sign in again.";

    /// <summary>What the page of the account says of the choice that is stored with it.</summary>
    public static string Stored(TimeDisplay display) =>
        $"Time zone: {NameOf(display)}. Your choice is stored with your account, and is in a download.";
}
