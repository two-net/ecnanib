namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the page of the appearance among the settings, in one place (NFR-USA-4). The
/// page has a route and a place in the navigation (FR-UI-2). The choice of a theme, which it is
/// for, is not built yet (FR-THM-1 to FR-THM-5). See docs/layout.md.
/// </summary>
public static class AppearancePages
{
    /// <summary>What decides the look of the pages for now.</summary>
    public const string FollowsDevice =
        "The pages are light or dark as your device is set, and change when its setting does.";

    /// <summary>What the page is to offer, and does not yet.</summary>
    public const string NotBuiltYet =
        "A choice of your own between dark, light, and the setting of your device, saved to your account, is not built yet.";
}
