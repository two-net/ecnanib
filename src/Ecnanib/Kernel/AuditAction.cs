namespace Ecnanib.Kernel;

/// <summary>
/// What an audit record says happened (FR-AUD-01). Each issue adds the actions it owns. Records store
/// the name, so a value is never renamed, and the Web UI gives each one its text. See Audit in
/// docs/architecture.md.
/// </summary>
public enum AuditAction
{
    SignedIn,

    /// <summary>A wrong password, a locked account, or an address with no account.</summary>
    SignInFailed,

    SignedOut,

    /// <summary>A user ended one of their other sessions.</summary>
    SessionRevoked,

    PasswordChanged,

    /// <summary>The bootstrap secret verified an administrator's address.</summary>
    AdministratorConfirmed,

    /// <summary>Someone signed in with a listed address entered the wrong bootstrap secret.</summary>
    AdministratorConfirmationFailed,

    /// <summary>The application started with a different active master key, and began re-encrypting secrets.</summary>
    MasterKeyRotated,

    /// <summary>An emailed link proved the user reads their address, through verification or a reset.</summary>
    EmailVerified,

    /// <summary>Someone asked for a reset link to be emailed to the account's address. By a visitor.</summary>
    PasswordResetRequested,

    /// <summary>A reset link set a new password, and ended every session.</summary>
    PasswordReset,

    /// <summary>An administrator issued a reset link to hand to the user, because email is off.</summary>
    PasswordResetLinkIssued,

    /// <summary>The user confirmed an authenticator, so sign-in and sensitive actions now need a code.</summary>
    TwoFactorEnabled,

    /// <summary>The user turned two-factor off, with a code.</summary>
    TwoFactorDisabled,

    /// <summary>The user replaced their recovery codes, with a code. The old ones stopped working.</summary>
    RecoveryCodesRegenerated,

    /// <summary>A recovery code stood in for an authenticator code, at sign-in or for a sensitive action, and is now spent.</summary>
    RecoveryCodeUsed,

    /// <summary>The user added an AI provider or Binance key, validated as it was saved.</summary>
    CredentialAdded,

    /// <summary>A key was checked with its provider again, and got an answer.</summary>
    CredentialValidated,

    /// <summary>The user replaced a key's secrets, keeping the credential.</summary>
    CredentialRotated,

    CredentialDeleted,

    /// <summary>The user created an agent, from scratch or as a clone of another, as a Draft.</summary>
    AgentCreated,

    /// <summary>The user changed an agent's settings.</summary>
    AgentUpdated,

    /// <summary>The user switched an agent between Autonomous and Confirm each trade (FR-AUD-01).</summary>
    AgentExecutionModeChanged,

    /// <summary>The agent passed its activation checks and became Active, from Draft or to resume it.</summary>
    AgentActivated,

    /// <summary>By the user, or by the system with a reason, such as a credential that stopped working.</summary>
    AgentPaused,

    /// <summary>The agent stopped running cycles, and left its positions and orders as they were.</summary>
    AgentStopped,

    /// <summary>
    /// The user used an agent's kill switch, alone or through "Stop all agents" (FR-AGT-08): it stopped, and its open
    /// orders are cancelled and its positions closed at market.
    /// </summary>
    AgentKillSwitch,

    /// <summary>
    /// The system put the agent in Error, because its decision cycles kept failing to reach a decision. Its
    /// positions and protective orders stay on the exchange.
    /// </summary>
    AgentErrored,

    /// <summary>The agent was archived, which is how agents are removed by default (DATA-R-04).</summary>
    AgentArchived,

    /// <summary>An archived agent was deleted for good.</summary>
    AgentDeleted,

    /// <summary>
    /// An administrator changed a model's overrides: its name, visibility, capabilities, price or recommended
    /// roles (FR-CAT-05). Platform-wide, so it has no user.
    /// </summary>
    CatalogModelEdited,

    /// <summary>An administrator changed a role's default provider or model (FR-ROLE-03, FR-ADM-03). Platform-wide.</summary>
    RoleDefaultChanged,

    /// <summary>An administrator started a sync of every user's AI keys (FR-CAT-03). Platform-wide.</summary>
    CatalogSyncRequested,

    /// <summary>
    /// The user approved a Confirm agent's trade (FR-APR-02, FR-AUD-01). The Risk Engine then checks it again before
    /// anything is sent, which can still reject it.
    /// </summary>
    TradeApproved,

    /// <summary>
    /// A Confirm agent's trade was rejected: by the user, or by the system when the Risk Engine's second look at an
    /// approved one failed (FR-APR-03, FR-AUD-01). Nothing was sent.
    /// </summary>
    TradeRejected,
}
