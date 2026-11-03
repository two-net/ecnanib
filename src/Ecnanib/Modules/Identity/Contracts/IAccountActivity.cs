using Ecnanib.Kernel;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// A user's own audit history (FR-AUD-03). It's here because the Web UI can't reach Platform, where
/// the trail lives. See Audit in docs/architecture.md.
/// </summary>
public interface IAccountActivity
{
    /// <summary>How many records <see cref="ListAsync"/> returns at most. Fewer means there are no older ones.</summary>
    int PageSize { get; }

    /// <summary>
    /// The records about this user's account, newest first. Pass the last ID of one page as
    /// <paramref name="before"/> to get the next.
    /// </summary>
    Task<IReadOnlyList<ActivityRecord>> ListAsync(Guid userId, long? before, CancellationToken cancellationToken);
}

/// <param name="Actor">Who acted. An administrator's identity isn't given, only that one acted.</param>
/// <param name="IpAddress">Null when the action came from no request, or the host didn't report one.</param>
/// <param name="UserAgent">What the client sent, or null. Anyone can write this, so only show it as text.</param>
public sealed record ActivityRecord(
    long Id,
    DateTimeOffset Timestamp,
    AuditAction Action,
    AuditActorKind Actor,
    string? IpAddress,
    string? UserAgent);
