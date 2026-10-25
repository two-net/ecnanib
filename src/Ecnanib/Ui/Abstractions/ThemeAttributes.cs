using Ecnanib.Database.Entities;

namespace Ecnanib.Ui.Abstractions;

/// <summary>
/// What the document element carries so the stylesheet can apply a preference before the first paint: <c>data-theme</c>
/// (absent for System, which lets <c>prefers-color-scheme</c> follow the operating system live), <c>data-motion</c>
/// (<c>reduced</c> or absent) and the <c>--ui-duration-base</c> token from which the stylesheet derives the
/// <c>--ui-duration</c> every transition reads; the derivation lets reduced motion zero it, which an inline value
/// could not be overridden to (FR-UI-02, FR-UI-03).
/// </summary>
public sealed record ThemeAttributes(string? Theme, string? Motion, string Style)
{
    public const string ThemeAttribute = "data-theme";

    public const string MotionAttribute = "data-motion";

    public const string Dark = "dark";

    public const string Light = "light";

    public const string Reduced = "reduced";

    public static ThemeAttributes For(UserPreferences preferences, int transitionMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new ThemeAttributes(
            preferences.Theme switch { UiTheme.Dark => Dark, UiTheme.Light => Light, _ => null },
            preferences.ReducedMotion ? Reduced : null,
            $"--ui-duration-base:{transitionMilliseconds}ms");
    }
}
