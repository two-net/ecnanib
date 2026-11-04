using Ecnanib.Kernel;

namespace Ecnanib.Components.Account;

/// <summary>
/// What each audit action is called on the activity page. Public so that a test can prove every
/// action has text: an issue that adds an action adds its text here, or the page fails.
/// </summary>
public static class ActivityText
{
    public static string Of(AuditAction action) => action switch
    {
        AuditAction.SignedIn => Strings.ActivityActionSignedIn,
        AuditAction.SignInFailed => Strings.ActivityActionSignInFailed,
        AuditAction.SignedOut => Strings.ActivityActionSignedOut,
        AuditAction.SessionRevoked => Strings.ActivityActionSessionRevoked,
        AuditAction.PasswordChanged => Strings.ActivityActionPasswordChanged,
        AuditAction.AdministratorConfirmed => Strings.ActivityActionAdministratorConfirmed,
        AuditAction.AdministratorConfirmationFailed => Strings.ActivityActionAdministratorConfirmationFailed,
        AuditAction.MasterKeyRotated => Strings.ActivityActionMasterKeyRotated,
        AuditAction.EmailVerified => Strings.ActivityActionEmailVerified,
        AuditAction.PasswordResetRequested => Strings.ActivityActionPasswordResetRequested,
        AuditAction.PasswordReset => Strings.ActivityActionPasswordReset,
        AuditAction.PasswordResetLinkIssued => Strings.ActivityActionPasswordResetLinkIssued,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Give the action its text in Strings.resx."),
    };

    /// <summary>Null when the user or a visitor acted, which the action already says.</summary>
    public static string? ByWhom(AuditActorKind actor) => actor switch
    {
        AuditActorKind.Administrator => Strings.ActivityByAdministrator,
        AuditActorKind.System => Strings.ActivityBySystem,
        _ => null,
    };
}
