using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Accounts;

/// <summary>
/// A security- or money-relevant action that the audit trail records (NFR-LOG-7). A member is
/// stored by its name, so renaming one changes stored data and needs a migration.
/// </summary>
/// <remarks>
/// A component adds the members of its own actions when it is built: the changes to a key, what
/// a user does with an agent, and what an operator does. See docs/accounts.md.
/// </remarks>
public enum AuditAction
{
    /// <summary>
    /// A browser was signed in to the account: a session started, with the password, and with a
    /// code as well for an account that has two-factor authentication on.
    /// </summary>
    SignedIn,

    /// <summary>The user proved again who they are, before a sensitive action (FR-ACC-4).</summary>
    Reauthenticated,

    /// <summary>
    /// The password of the account was replaced: with a reset link, or with the link of an
    /// address that was registered again.
    /// </summary>
    PasswordChanged,

    /// <summary>Two-factor authentication was turned on, with an authenticator app.</summary>
    TwoFactorTurnedOn,

    /// <summary>Two-factor authentication was turned off.</summary>
    TwoFactorTurnedOff,

    /// <summary>The recovery codes of the account were replaced by new ones.</summary>
    TwoFactorRecoveryCodesReplaced,

    /// <summary>A recovery code was used in place of a code from the app, and is spent.</summary>
    TwoFactorRecoveryCodeUsed,

    /// <summary>
    /// A key of an AI provider or of Binance was stored where none was (FR-KEY-2). The target
    /// names the key by its id, and the event never holds the key.
    /// </summary>
    KeyAdded,

    /// <summary>A key was stored in place of the one that was stored (FR-KEY-10).</summary>
    KeyReplaced,

    /// <summary>A stored key was deleted, for good (FR-KEY-10).</summary>
    KeyDeleted,

    /// <summary>
    /// A stored Binance key was deleted by the service, for good: when it was checked again,
    /// Binance reported that it can withdraw, and Ecnanib holds no such key (FR-KEY-4,
    /// NFR-LEG-2). The event carries the owner of the key, and no origin: no request did it.
    /// </summary>
    KeyRejected,

    /// <summary>
    /// A risk setting of an agent was changed: its risk per trade, its minimum reward-to-risk
    /// ratio, its leverage cap, its liquidation buffer, its daily loss limit, its drawdown limit,
    /// or how far an entry may be from the price (FR-RSK-2). The target names the agent by its
    /// id, and the event holds no number.
    /// </summary>
    AgentRiskSettingsChanged,

    /// <summary>
    /// The user started an agent: one that was a draft, or was stopped (FR-AGT-1). The target
    /// names the agent by its id.
    /// </summary>
    AgentStarted,

    /// <summary>The user paused an agent that was running.</summary>
    AgentPaused,

    /// <summary>
    /// The user resumed an agent that was paused, was halted, or required attention. It runs
    /// again, as after a start.
    /// </summary>
    AgentResumed,

    /// <summary>The user stopped an agent.</summary>
    AgentStopped,

    /// <summary>
    /// The user asked for the position of an agent to be closed at market, by hand: with "Close
    /// position now", or as they stopped the agent (FR-AGT-11, FR-AGT-12). It is recorded when
    /// it is asked for, before the order: what places the order records nothing here.
    /// </summary>
    AgentPositionCloseRequested,

    /// <summary>
    /// The user switched an agent from Demo to Live, where it trades with real money: with a
    /// valid Live key, a risk confirmation, and a fresh proof of who they are (FR-AGT-7). The
    /// target names the agent by its id.
    /// </summary>
    AgentSwitchedToLive,

    /// <summary>The user switched an agent from Live back to Demo.</summary>
    AgentSwitchedToDemo,

    /// <summary>
    /// An operator switched the global halt on: from then on no agent opens a new position, and
    /// open positions are still managed and protected (FR-OPS-3, FR-OPS-5). The target names the
    /// halt, and the event carries the operator as its user.
    /// </summary>
    GlobalHaltSwitchedOn,

    /// <summary>An operator switched the global halt off: agents open positions again.</summary>
    GlobalHaltSwitchedOff,

    /// <summary>
    /// An operator disabled the account of a user: it is not signed in from then on, and its
    /// sessions ended (FR-OPS-4, FR-OPS-5). The event carries the operator as its user, and the
    /// target names the account that was disabled, which is another one.
    /// </summary>
    AccountDisabled,

    /// <summary>An operator enabled an account that was disabled: its user can sign in again.</summary>
    AccountEnabled,

    /// <summary>
    /// An operator asked for a sync of the model catalog, outside its schedule (FR-CAT-2,
    /// FR-OPS-4, FR-OPS-5). It is recorded when it is asked for, before the providers are asked:
    /// what syncs the catalog may not use Accounts, and records each sync in a table of its own.
    /// The target names the catalog, and the event carries the operator as its user.
    /// </summary>
    CatalogSyncRequested,
}

/// <summary>
/// One action in the audit trail, as a row of the <c>audit_events</c> table (NFR-LOG-7). The audit
/// trail is in the database and is not a log. An event holds no secret. See docs/accounts.md.
/// </summary>
/// <remarks>
/// An event is made by <see cref="AuditTrail.RecordAsync"/> and by nothing else. Its constructor
/// is private, and what makes one takes no text: so nothing that a request brought, such as a
/// key or a password, can end up in an event.
/// </remarks>
public sealed class AuditEvent : IUserOwned
{
    // What a row is read with. The model finds it, private as it is.
    private AuditEvent(Guid id, Guid userId, AuditAction action, string target, DateTimeOffset occurredAt, string? origin)
    {
        Id = id;
        UserId = userId;
        Action = action;
        Target = target;
        OccurredAt = occurredAt;
        Origin = origin;
    }

    /// <summary>Assigned by the application when the action is recorded. A later event has a greater id.</summary>
    public Guid Id { get; }

    /// <summary>
    /// The user who did it, or the user it was done for when the service did it: the owner of a
    /// key that the service deleted.
    /// </summary>
    public Guid UserId { get; }

    /// <summary>What was done.</summary>
    public AuditAction Action { get; }

    /// <summary>
    /// What it was done to, as a kind and an id: <c>user:…</c> for an account. What there is one
    /// of in the service has a name in place of an id: <c>runtime-flag:global-halt</c> for the
    /// global halt, and <c>catalog:models</c> for the model catalog. It is the
    /// account of <see cref="UserId"/> for what a user does to their own, and the account of
    /// another user for what an operator does to it.
    /// </summary>
    public string Target { get; }

    /// <summary>When it was done.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// The address the request came from, or null when it is not known. It is personal data, and
    /// goes with the account.
    /// </summary>
    public string? Origin { get; }

    /// <summary>An event, from values that cannot hold what a request brought.</summary>
    internal static AuditEvent Of(
        Guid id, Guid userId, AuditAction action, AuditTarget target, DateTimeOffset occurredAt, AuditOrigin origin) =>
        new(id, userId, action, target.Text, occurredAt, origin.Address);
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(audited => audited.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(audited => audited.Id).ValueGeneratedNever();
        builder.Property(audited => audited.UserId);
        builder.Property(audited => audited.Action);
        builder.Property(audited => audited.Target);
        builder.Property(audited => audited.OccurredAt);
        builder.Property(audited => audited.Origin);

        // The events of every user by when they happened, the oldest first: what the job that
        // deletes the ones that are kept no longer reads, once for every batch (NFR-DAT-5).
        builder.HasIndex(audited => audited.OccurredAt);

        // The events of an account go with it when it is deleted: they hold where its user was.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(audited => audited.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
