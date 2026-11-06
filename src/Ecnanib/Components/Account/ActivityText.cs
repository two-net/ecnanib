using System.Globalization;
using Ecnanib.Components.Agents;
using Ecnanib.Components.Credentials;
using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Identity.Contracts;

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
        AuditAction.TwoFactorEnabled => Strings.ActivityActionTwoFactorEnabled,
        AuditAction.TwoFactorDisabled => Strings.ActivityActionTwoFactorDisabled,
        AuditAction.RecoveryCodesRegenerated => Strings.ActivityActionRecoveryCodesRegenerated,
        AuditAction.RecoveryCodeUsed => Strings.ActivityActionRecoveryCodeUsed,
        AuditAction.CredentialAdded => Strings.ActivityActionCredentialAdded,
        AuditAction.CredentialValidated => Strings.ActivityActionCredentialValidated,
        AuditAction.CredentialRotated => Strings.ActivityActionCredentialRotated,
        AuditAction.CredentialDeleted => Strings.ActivityActionCredentialDeleted,
        AuditAction.AgentCreated => Strings.ActivityActionAgentCreated,
        AuditAction.AgentUpdated => Strings.ActivityActionAgentUpdated,
        AuditAction.AgentExecutionModeChanged => Strings.ActivityActionAgentExecutionModeChanged,
        AuditAction.AgentActivated => Strings.ActivityActionAgentActivated,
        AuditAction.AgentPaused => Strings.ActivityActionAgentPaused,
        AuditAction.AgentStopped => Strings.ActivityActionAgentStopped,
        AuditAction.AgentErrored => Strings.ActivityActionAgentErrored,
        AuditAction.AgentArchived => Strings.ActivityActionAgentArchived,
        AuditAction.AgentDeleted => Strings.ActivityActionAgentDeleted,
        AuditAction.CatalogModelEdited => Strings.ActivityActionCatalogModelEdited,
        AuditAction.RoleDefaultChanged => Strings.ActivityActionRoleDefaultChanged,
        AuditAction.CatalogSyncRequested => Strings.ActivityActionCatalogSyncRequested,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Give the action its text in Strings.resx."),
    };

    /// <summary>
    /// What a field in a before/after summary is called, or null for one with no text, which the page then
    /// leaves out. An issue that records a summary a user sees gives each field its text here.
    /// </summary>
    public static string? FieldOf(string field) => field switch
    {
        CredentialAuditFields.Provider => Strings.ActivityFieldProvider,
        CredentialAuditFields.Environment => Strings.ActivityFieldEnvironment,
        CredentialAuditFields.Label => Strings.ActivityFieldLabel,
        CredentialAuditFields.Status => Strings.ActivityFieldStatus,
        CredentialAuditFields.Ending => Strings.ActivityFieldEnding,
        _ => AgentText.AuditFieldOf(field),
    };

    /// <summary>
    /// One field's change as a line of text, or null when the field has no text. A value that was added or
    /// removed shows alone, and so does one that stayed the same, such as a status a check confirmed.
    /// </summary>
    public static string? Describe(ActivityChange change)
    {
        var before = ValueOf(change.Field, change.Before);
        var after = ValueOf(change.Field, change.After);

        if (FieldOf(change.Field) is null || (before is null && after is null))
        {
            return null;
        }

        return before is null || after is null || before == after
            ? after ?? before
            : string.Format(CultureInfo.CurrentCulture, Strings.ActivityChangeFromTo, before, after);
    }

    /// <summary>A value as recorded, except the names the Web UI has words for. Only exact names count.</summary>
    private static string? ValueOf(string field, string? value) => (field, value) switch
    {
        (_, null) => null,
        (CredentialAuditFields.Status, _) when Enum.GetNames<CredentialStatus>().Contains(value, StringComparer.Ordinal) =>
            CredentialText.StatusOf(Enum.Parse<CredentialStatus>(value)),
        (CredentialAuditFields.Environment, _) when Enum.GetNames<TradingEnvironment>().Contains(value, StringComparer.Ordinal) =>
            CredentialText.EnvironmentOf(Enum.Parse<TradingEnvironment>(value)),
        (CredentialAuditFields.Ending, _) => CredentialText.Ending(value),
        _ => AgentText.AuditValueOf(field, value) ?? value,
    };

    /// <summary>Null when the user or a visitor acted, which the action already says.</summary>
    public static string? ByWhom(AuditActorKind actor) => actor switch
    {
        AuditActorKind.Administrator => Strings.ActivityByAdministrator,
        AuditActorKind.System => Strings.ActivityBySystem,
        _ => null,
    };
}
