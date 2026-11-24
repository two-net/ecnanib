using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Themes;

/// <summary>
/// The theme that a page is written in: dark, light, or as the device is set (FR-THM-1). The
/// server decides it for every page that it answers with, and writes it into the first element
/// of the page, so the page is in its theme before anything of it is painted, and no script has
/// a part in it (FR-THM-4). See docs/themes.md.
/// </summary>
public static class PageTheme
{
    /// <summary>
    /// The theme of the page that a request is answered with. For a user who is signed in it is
    /// the theme of their account, on every device. For a visitor it is the theme that their
    /// device saved (FR-THM-3). Whoever has chosen nothing has <see cref="Theme.System"/>.
    /// </summary>
    public static Theme Of(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SignedInUser.ThemeOf(request.User) ?? DeviceTheme.Of(request.Request);
    }

    /// <summary>
    /// What a page says of its theme to the stylesheet, as <c>data-theme</c> on its first
    /// element: <c>dark</c> or <c>light</c>. A page that looks as the device is set says
    /// nothing, and the stylesheet follows the device (FR-THM-2).
    /// </summary>
    public static string? AttributeOf(Theme theme) => theme == Theme.System ? null : WordOf(theme);

    /// <summary>
    /// What a page tells the browser of its colours ahead of the stylesheet, as the content of
    /// <c>&lt;meta name="color-scheme"&gt;</c>: which of light and dark the page may be. So the
    /// browser paints nothing in the other one while the stylesheet is on its way (FR-THM-4).
    /// </summary>
    public static string ColorSchemeOf(Theme theme) => theme switch
    {
        Theme.Dark => "dark",
        Theme.Light => "light",
        _ => "light dark",
    };

    /// <summary>
    /// A theme as a form, a cookie, and a page spell it: <c>system</c>, <c>dark</c>, or
    /// <c>light</c>.
    /// </summary>
    public static string WordOf(Theme theme) => theme switch
    {
        Theme.Dark => "dark",
        Theme.Light => "light",
        _ => "system",
    };

    /// <summary>The theme that a word names, or null for anything that is no such word.</summary>
    public static Theme? OfWord(string? word) => word switch
    {
        "system" => Theme.System,
        "dark" => Theme.Dark,
        "light" => Theme.Light,
        _ => null,
    };
}
