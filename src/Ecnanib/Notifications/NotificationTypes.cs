namespace Ecnanib.Notifications;

/// <summary>
/// What each kind of notification is called for its user, and which kinds are critical events,
/// which a user can have sent by email (FR-NTF-1, FR-NTF-3). See docs/notifications.md.
/// </summary>
/// <remarks>
/// It is here, and not with the pages: the email of a critical event says the same words as the
/// page, and Notifications may not use the Web UI.
/// </remarks>
public static class NotificationTypes
{
    /// <summary>What a notification is about, as its user reads it (FR-NTF-1).</summary>
    public static string Describe(NotificationType type) => type switch
    {
        NotificationType.ProtectionFailure => "Order or protection failure",
        NotificationType.EntrySkipped => "Entry skipped",
        NotificationType.AttentionRequired => "Agent needs attention",
        NotificationType.LiquidationWarning => "Liquidation warning",
        NotificationType.ReconciliationMismatch => "Reconciliation mismatch",
        NotificationType.AgentHalted => "Agent halted",
        NotificationType.RolesReassigned => "Roles reassigned",
        NotificationType.PositionOpened => "Position opened",
        NotificationType.PositionClosed => "Position closed",
        NotificationType.KeyInvalid => "Key invalid",
        NotificationType.ModelUnavailable => "Model unavailable",
        NotificationType.AgentResumed => "Agent runs again",
        NotificationType.ExchangeOutage => "Exchange outage",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The type has no description."),
    };

    /// <summary>
    /// Whether a kind of notification is a critical event: one that stops an agent from trading,
    /// or that puts money at risk. A user who opted in is sent an email for it (FR-NTF-3).
    /// </summary>
    /// <remarks>
    /// The specification names no kind. An agent that is halted or waits for its user, a key
    /// that is invalid, and a model that is unavailable stop an agent. An exchange that is out
    /// stops every agent of a market from opening a position, and its user is told the same
    /// way when it answers again. A failure of an order or of the protection of a position, and
    /// a position that approaches liquidation, put money at risk. The others say what went as
    /// it should, or what the service put right.
    /// </remarks>
    public static bool IsCritical(NotificationType type) => type switch
    {
        NotificationType.AgentHalted
            or NotificationType.AttentionRequired
            or NotificationType.KeyInvalid
            or NotificationType.ModelUnavailable
            or NotificationType.ProtectionFailure
            or NotificationType.LiquidationWarning
            or NotificationType.ExchangeOutage => true,
        NotificationType.EntrySkipped
            or NotificationType.ReconciliationMismatch
            or NotificationType.RolesReassigned
            or NotificationType.PositionOpened
            or NotificationType.PositionClosed
            or NotificationType.AgentResumed => false,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The type is neither critical nor not."),
    };
}
