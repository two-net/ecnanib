using Ecnanib.Kernel;

namespace Ecnanib.Components;

/// <summary>
/// What each notification type is called in the app: on the list, and beside its box on the preferences
/// page. Public so that a test can prove every type has text: a type added to the Kernel adds its text
/// here, or the page fails. The email has its own, in Modules/Notifications/Emails.resx.
/// </summary>
public static class NotificationText
{
    public static string Of(NotificationType type) => type switch
    {
        NotificationType.TradeOpened => Strings.NotificationTitleTradeOpened,
        NotificationType.TradeClosed => Strings.NotificationTitleTradeClosed,
        NotificationType.StopMoved => Strings.NotificationTitleStopMoved,
        NotificationType.ApprovalPending => Strings.NotificationTitleApprovalPending,
        NotificationType.ApprovalExpired => Strings.NotificationTitleApprovalExpired,
        NotificationType.AgentPausedLossLimit => Strings.NotificationTitleAgentPausedLossLimit,
        NotificationType.AgentPausedBudget => Strings.NotificationTitleAgentPausedBudget,
        NotificationType.AgentPausedErrors => Strings.NotificationTitleAgentPausedErrors,
        NotificationType.CredentialInvalid => Strings.NotificationTitleCredentialInvalid,
        NotificationType.ModelDeprecated => Strings.NotificationTitleModelDeprecated,
        NotificationType.ModelRetired => Strings.NotificationTitleModelRetired,
        NotificationType.KillSwitchExecuted => Strings.NotificationTitleKillSwitchExecuted,
        NotificationType.CatalogSyncFailed => Strings.NotificationTitleCatalogSyncFailed,
        NotificationType.ExchangeRateLimitBan => Strings.NotificationTitleExchangeRateLimitBan,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Give the type its text in Strings.resx."),
    };

    /// <summary>
    /// How the preferences page groups the email choices, in order, with the types in each. Every type
    /// is in exactly one group, which a test checks.
    /// </summary>
    public static IReadOnlyList<(string Legend, NotificationType[] Types)> Groups =>
    [
        (Strings.PreferencesEmailGroupTrades, [NotificationType.TradeOpened, NotificationType.TradeClosed, NotificationType.StopMoved]),
        (Strings.PreferencesEmailGroupApprovals, [NotificationType.ApprovalPending, NotificationType.ApprovalExpired]),
        (
            Strings.PreferencesEmailGroupAgents,
            [
                NotificationType.AgentPausedLossLimit,
                NotificationType.AgentPausedBudget,
                NotificationType.AgentPausedErrors,
                NotificationType.KillSwitchExecuted,
            ]
        ),
        (
            Strings.PreferencesEmailGroupKeysAndModels,
            [NotificationType.CredentialInvalid, NotificationType.ModelDeprecated, NotificationType.ModelRetired]
        ),
        (Strings.PreferencesEmailGroupAdministration, [NotificationType.CatalogSyncFailed, NotificationType.ExchangeRateLimitBan]),
    ];
}
