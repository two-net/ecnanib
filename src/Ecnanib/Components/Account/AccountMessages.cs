using Ecnanib.Accounts.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Ui.Abstractions;

namespace Ecnanib.Components.Account;

/// <summary>User-facing texts shared by the account pages; the disclaimer's own texts are in <c>RiskDisclaimer</c>.</summary>
internal static class AccountMessages
{
    public const string IncorrectCredentials = "Incorrect email or password.";

    /// <summary>The settings page's status line (FR-UI-01): the version accepted and when, stamped in UTC (NFR-USE-03).</summary>
    public static string DisclaimerAcceptedOn(string version, DateTimeOffset at) => $"You accepted version {version} on {UtcStamp.Minute(at)}.";

    /// <summary>An earlier version was accepted and the text changed since (FR-ACC-08: every user is asked again).</summary>
    public static string DisclaimerChanged(DateTimeOffset last) => $"The disclaimer has changed since you accepted it on {UtcStamp.Minute(last)}; please read and accept it again.";

    public const string ThemeHint = "System follows your device's light or dark setting, also when it changes while you are here.";

    public const string ReducedMotionLabel = "Reduce animation";

    public const string ReducedMotionHint = "Keeps live values and status changes, stops decorative motion. Your device's reduced-motion setting is always honoured.";

    public const string PreferencesSaved = "Saved. The choice applies on every device you sign in on.";

    public const string PreferencesNotSaved = "The choice could not be saved. Try again.";

    public const string PreferencesSignedOut = "You are signed out. Sign in again to change the appearance.";

    public const string PreferencesNeedScript = "Changing the appearance needs JavaScript; without it the page follows your device.";

    public static string TooManyAttempts(TimeSpan? retryAfter)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((retryAfter ?? TimeSpan.Zero).TotalMinutes));
        return $"Too many attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
    }

    public static string PasswordRule(int minLength) =>
        $"The password must be between {minLength} and {PasswordPolicy.MaxLength} characters.";

    public static string ThemeLabel(UiTheme theme) => theme switch
    {
        UiTheme.Dark => "Dark",
        UiTheme.Light => "Light",
        _ => "Follow the system",
    };
}
