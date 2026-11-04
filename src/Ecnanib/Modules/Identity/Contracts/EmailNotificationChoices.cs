using Ecnanib.Kernel;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// Which notifications email can carry, and to whom (FR-PREF-06). In-app notifications are always on,
/// so this only decides email. See Notifications in docs/architecture.md.
/// </summary>
public static class EmailNotificationChoices
{
    /// <summary>
    /// What a user who has never chosen is emailed: the events that need them to act. Trades, stop moves,
    /// expired approvals and deprecations stay in the app until they choose them.
    /// </summary>
    public static IReadOnlySet<NotificationType> Defaults { get; } = new HashSet<NotificationType>
    {
        NotificationType.ApprovalPending,
        NotificationType.AgentPausedLossLimit,
        NotificationType.AgentPausedBudget,
        NotificationType.AgentPausedErrors,
        NotificationType.CredentialInvalid,
        NotificationType.ModelRetired,
        NotificationType.KillSwitchExecuted,
        NotificationType.CatalogSyncFailed,
        NotificationType.ExchangeRateLimitBan,
    };

    /// <summary>Raised for administrators, and never offered to anyone else (FR-NTF-01).</summary>
    public static IReadOnlySet<NotificationType> AdministratorOnly { get; } = new HashSet<NotificationType>
    {
        NotificationType.CatalogSyncFailed,
        NotificationType.ExchangeRateLimitBan,
    };

    /// <summary>The types someone can be notified of, in declaration order.</summary>
    public static IReadOnlyList<NotificationType> AvailableTo(bool administrator) =>
        [.. Enum.GetValues<NotificationType>().Where(type => administrator || !AdministratorOnly.Contains(type))];
}
