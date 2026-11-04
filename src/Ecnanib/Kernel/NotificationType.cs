namespace Ecnanib.Kernel;

/// <summary>
/// What a notification is about (FR-NTF-01). The SRS fixes the list, and Identity's email choices,
/// Notifications, the Web UI and every feature that raises one use it. Rows and email choices store
/// the name, so a value is never renamed, and the Web UI and the email each give it text. See
/// Notifications in docs/architecture.md.
/// </summary>
public enum NotificationType
{
    TradeOpened,

    /// <summary>By the agent, by its stop or target, or outside Ecnanib (FR-EXE-06).</summary>
    TradeClosed,

    StopMoved,

    /// <summary>A trade waits for the user in Confirm mode (FR-APR-01).</summary>
    ApprovalPending,

    ApprovalExpired,

    /// <summary>A daily or weekly loss limit, or the consecutive-loss count (FR-RISK-08).</summary>
    AgentPausedLossLimit,

    /// <summary>The AI budget ran out, so the agent only manages its protective orders (FR-CYC-07).</summary>
    AgentPausedBudget,

    AgentPausedErrors,

    /// <summary>A credential failed validation, and its agents paused (FR-KEY-11).</summary>
    CredentialInvalid,

    /// <summary>A model an agent uses stopped being listed by its provider (FR-CAT-04).</summary>
    ModelDeprecated,

    ModelRetired,

    KillSwitchExecuted,

    /// <summary>For administrators only.</summary>
    CatalogSyncFailed,

    /// <summary>For administrators only. Binance refused requests from the server's address for a while.</summary>
    ExchangeRateLimitBan,
}
