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

    /// <summary>What it was done to, as a kind and an id: <c>user:…</c> for an account.</summary>
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

        // The events of an account go with it when it is deleted: they hold where its user was.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(audited => audited.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
