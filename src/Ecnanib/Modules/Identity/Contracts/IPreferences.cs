namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// A user's theme, animation level and display time zone (SRS 3.2). The page applies them on its next
/// full render, because only that writes &lt;html&gt;: see Preferences in docs/architecture.md.
/// </summary>
public interface IPreferences
{
    /// <summary>
    /// What a page shows before sign-in: the System theme whatever the configuration says (FR-PREF-03),
    /// the configured animation level, and the browser's time zone.
    /// </summary>
    UserPreferences ForVisitors { get; }

    /// <summary>The user's choices, or the configured defaults for anyone who hasn't saved any.</summary>
    Task<UserPreferences> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Saves nothing unless the time zone is null or one of <see cref="TimeZones.Offered"/>.</summary>
    Task<PreferencesResult> SaveAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken);
}

/// <summary>FR-PREF-01. Stored by name, so never rename a value.</summary>
public enum Theme
{
    /// <summary>Follows the operating system's color scheme, live (FR-PREF-02).</summary>
    System,
    Light,
    Dark,
}

/// <summary>FR-PREF-04. Stored by name, so never rename a value.</summary>
public enum AnimationLevel
{
    /// <summary>Reduced when the operating system asks for reduced motion, and Full otherwise.</summary>
    System,
    Full,

    /// <summary>Fades replace motion (FR-ANI-07).</summary>
    Reduced,

    /// <summary>No non-essential animation (FR-ANI-07).</summary>
    Off,
}

/// <param name="TimeZone">An IANA time zone ID, or null for the browser's (FR-PREF-05).</param>
public sealed record UserPreferences(Theme Theme, AnimationLevel Animation, string? TimeZone);

public enum PreferencesResult
{
    Saved,

    /// <summary>The time zone isn't one of <see cref="TimeZones.Offered"/>, so nothing was saved.</summary>
    UnknownTimeZone,
}
