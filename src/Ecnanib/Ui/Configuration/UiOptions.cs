using System.ComponentModel.DataAnnotations;
using Ecnanib.Database.Entities;
using Ecnanib.Ui.Abstractions;

namespace Ecnanib.Ui.Configuration;

/// <summary>
/// The <c>ui</c> section of the system configuration file (Appendix D): the theme and animation defaults that apply
/// to visitors and to new accounts until the user chooses otherwise on the settings page (C-06, FR-UI-02, FR-UI-03).
/// </summary>
public sealed class UiOptions
{
    public const string SectionName = "Ui";

    /// <summary>Theme for visitors and new accounts: <c>System</c> (the default FR-UI-02 requires), <c>Dark</c> or <c>Light</c>; bound as text so a misspelling is a validation failure, not a binding exception.</summary>
    [Required(AllowEmptyStrings = false)]
    public string DefaultTheme { get; set; } = "System";

    /// <summary>Animation default for visitors and new accounts: <c>true</c> disables non-essential animation as the in-app toggle does (FR-UI-03, NFR-USE-02).</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>Length of UI transitions in milliseconds, emitted as the <c>--ui-duration</c> style token; FR-UI-03 caps it at 400.</summary>
    [Range(1, 400)]
    public int TransitionMilliseconds { get; set; } = 240;

    /// <summary>The parsed <see cref="DefaultTheme"/>; <see cref="UiTheme.System"/> when the text is not a theme name (validation refuses the file before that matters).</summary>
    public UiTheme Theme => TryParseTheme(DefaultTheme) ?? UiTheme.System;

    /// <summary>What a visitor or a new account gets.</summary>
    public UserPreferences Defaults => new(Theme, ReducedMotion);

    /// <summary>A theme by name, case-insensitively; numbers and unknown names are null.</summary>
    public static UiTheme? TryParseTheme(string? value)
    {
        foreach (var theme in Enum.GetValues<UiTheme>())
        {
            if (string.Equals(theme.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
                return theme;
        }

        return null;
    }
}
