using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Accounts;

/// <summary>
/// A security- or money-relevant action that the audit trail records (NFR-LOG-7). A member is
/// stored by its name, so renaming one changes stored data and needs a migration.
/// </summary>
public enum AuditAction
{
    /// <summary>Two-factor authentication was turned on, with an authenticator app.</summary>
    TwoFactorTurnedOn,

    /// <summary>Two-factor authentication was turned off.</summary>
    TwoFactorTurnedOff,

    /// <summary>The recovery codes of the account were replaced by new ones.</summary>
    TwoFactorRecoveryCodesReplaced,

    /// <summary>A recovery code was used in place of a code from the app, and is spent.</summary>
    TwoFactorRecoveryCodeUsed,
}

/// <summary>
/// One action in the audit trail, as a row of the <c>audit_events</c> table (NFR-LOG-7). The audit
/// trail is in the database and is not a log. An event holds no secret. See docs/accounts.md.
/// </summary>
/// <param name="id">Assigned by the application when the action is recorded.</param>
/// <param name="userId">The user who did it.</param>
/// <param name="action">What was done.</param>
/// <param name="target">What it was done to, as a kind and an id: <c>user:…</c> for an account.</param>
/// <param name="occurredAt">When it was done.</param>
/// <param name="origin">
/// The address the request came from, or null when it is not known. It is personal data, and goes
/// with the account.
/// </param>
public sealed class AuditEvent(
    Guid id, Guid userId, AuditAction action, string target, DateTimeOffset occurredAt, string? origin)
{
    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public AuditAction Action { get; } = action;

    public string Target { get; } = target;

    public DateTimeOffset OccurredAt { get; } = occurredAt;

    public string? Origin { get; } = origin;
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
