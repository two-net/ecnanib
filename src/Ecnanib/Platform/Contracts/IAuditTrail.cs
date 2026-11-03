using Ecnanib.Kernel;

namespace Ecnanib.Platform.Contracts;

/// <summary>
/// The audit trail (FR-AUD-01, FR-AUD-02): who did what, from where. It's append-only, so there's
/// nothing here to change a record, and the table refuses UPDATE and TRUNCATE. See Audit in
/// docs/architecture.md.
/// </summary>
public interface IAuditTrail
{
    /// <summary>
    /// Adds a record to <paramref name="context"/> without saving, so the caller's save commits it
    /// with the change it describes. Across several statements, put both in one transaction. It
    /// stamps the time, the correlation ID, and the client's address and user agent, which are null
    /// outside a request or circuit action. Never put a secret in <see cref="AuditEntry.Changes"/>:
    /// masking there is a backstop, and it can't recognise a secret with no marker.
    /// </summary>
    void Add(EcnanibDbContext context, AuditEntry entry);

    /// <summary>
    /// The records about <paramref name="userId"/>'s account, newest first. Pass the last ID of one
    /// page as <paramref name="before"/> to get the next.
    /// </summary>
    Task<IReadOnlyList<AuditRecord>> ListForUserAsync(Guid userId, long? before, int count, CancellationToken cancellationToken);
}

/// <summary>What happened, as the caller knows it. The trail adds when, where from and the correlation ID.</summary>
public sealed record AuditEntry
{
    public required AuditAction Action { get; init; }

    public required AuditActorKind ActorKind { get; init; }

    /// <summary>The user or administrator who acted. Null for the system and for a visitor.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>
    /// The account the record is about, whose history shows it. An administrator acting on someone
    /// sets that person here. Null for a platform-wide action, or a sign-in to an address with no account.
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>What was acted on, such as <c>Session</c>, when it isn't just the account.</summary>
    public string? TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>The before/after summary. Values only, never secrets.</summary>
    public IReadOnlyList<AuditChange> Changes { get; init; } = [];

    /// <summary>A user acting on their own account.</summary>
    public static AuditEntry ByUser(AuditAction action, Guid userId) =>
        new() { Action = action, ActorKind = AuditActorKind.User, ActorId = userId, UserId = userId };
}

/// <param name="Field">A name the Web UI can give text to. A name that looks like a secret's has its values masked.</param>
/// <param name="Before">Null when the field had no value, as when something is created.</param>
/// <param name="After">Null when the field has no value now, as when something is deleted.</param>
public sealed record AuditChange(string Field, string? Before, string? After);

/// <param name="IpAddress">Null outside a request or circuit action, or when the host didn't report one.</param>
/// <param name="UserAgent">What the client sent, or null. Anyone can write this, so only show it as text.</param>
public sealed record AuditRecord(
    long Id,
    DateTimeOffset Timestamp,
    AuditAction Action,
    AuditActorKind ActorKind,
    Guid? ActorId,
    Guid? UserId,
    string? TargetType,
    string? TargetId,
    IReadOnlyList<AuditChange> Changes,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId);
