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
/// <param name="Changes">The before/after summary (FR-AUD-02), empty when the action has none.</param>
public sealed record ActivityRecord(
    long Id,
    DateTimeOffset Timestamp,
    AuditAction Action,
    AuditActorKind Actor,
    string? IpAddress,
    string? UserAgent,
    IReadOnlyList<ActivityChange> Changes);

/// <summary>One field of an action's before/after summary, with secrets already masked.</summary>
/// <param name="Field">A name the owning module defines, which the Web UI gives text to.</param>
/// <param name="Before">Null when it had no value, as when something is added.</param>
/// <param name="After">Null when it has no value now, as when something is deleted.</param>
/// <remarks>Values can be user text, such as a key's label, so only show them as text.</remarks>
public sealed record ActivityChange(string Field, string? Before, string? After);
