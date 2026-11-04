using Ecnanib.Kernel;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// A user's theme, animation level and display time zone (SRS 3.2), and which notifications they're
/// emailed (FR-PREF-06). The page applies the first three on its next full render, because only that
/// writes &lt;html&gt;: see Preferences in docs/architecture.md.
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

    /// <summary>Saves nothing unless the time zone is null or one of <see cref="TimeZones.Offered"/>. Email choices stay as they are.</summary>
    Task<PreferencesResult> SaveAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken);

    /// <summary>
    /// The same, and also the notifications the user wants by email, unless
    /// <paramref name="emailNotifications"/> is null or email is off, when the stored choices stay as they
    /// are. An empty set means none. Saves nothing if the set holds a type the user can't receive.
    /// </summary>
    Task<PreferencesResult> SaveAsync(
        Guid userId,
        UserPreferences preferences,
        IReadOnlySet<NotificationType>? emailNotifications,
        CancellationToken cancellationToken);

    /// <summary>
    /// What the user has chosen to be emailed, or <see cref="EmailNotificationChoices.Defaults"/> if they
    /// never chose, without the administrator-only types unless they're an administrator. Whether anything
    /// is sent also depends on email being on and the address being verified.
    /// </summary>
    Task<IReadOnlySet<NotificationType>> GetEmailNotificationsAsync(Guid userId, CancellationToken cancellationToken);
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

    /// <summary>An email choice is for administrators only, and the user isn't one, so nothing was saved.</summary>
    UnavailableEmailNotification,
}
