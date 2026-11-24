using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the two pages of the appearance, in one place (NFR-USA-4): the one among the
/// settings, where a user chooses the theme of their account, and the one of a visitor, who
/// chooses the theme of their device (FR-THM-1, FR-THM-3, FR-UI-2). See docs/themes.md.
/// </summary>
public static class AppearancePages
{
    /// <summary>The heading of the page of a visitor. The page of a user is named by its screen.</summary>
    public const string Title = "Appearance";

    /// <summary>The name of the form that saves the choice, on either page.</summary>
    public const string Form = "theme";

    /// <summary>The name of the field of that form that says which theme was chosen.</summary>
    public const string Field = "Theme";

    /// <summary>What the choices are a choice of.</summary>
    public const string Legend = "Theme";

    /// <summary>The button that saves the choice.</summary>
    public const string Save = "Save";

    /// <summary>What a form is told that names no theme.</summary>
    public const string ChooseOne = "Choose System, Dark, or Light.";

    /// <summary>What a user is told about where their choice holds.</summary>
    public const string SavedToAccount =
        "Your choice is saved to your account, and the pages look that way on every device you sign in on.";

    /// <summary>What a visitor is told about where their choice holds.</summary>
    public const string SavedOnDevice =
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
}
