using System.Net;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>A user's own sessions (FR-AUTH-07). See Sessions in docs/architecture.md.</summary>
public interface ISessions
{
    /// <summary>The sessions that haven't expired, the most recently used first.</summary>
    Task<IReadOnlyList<SessionRecord>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Ends one of the user's other sessions and closes its connections, which ends its circuits. False,
    /// changing nothing, when it isn't this user's or has already ended.
    /// </summary>
    Task<bool> RevokeAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="RevokeAsync"/> for the session the user is signing out of, which the audit trail
    /// records as signing out. Clearing the cookie is the caller's job.
    /// </summary>
    Task<bool> SignOutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
}

/// <param name="Id">Only useful with <see cref="ISessions.RevokeAsync"/>, which checks the owner.</param>
/// <param name="CreatedAt">When the user signed in.</param>
/// <param name="LastSeenAt">The last request or user action, to within a minute.</param>
/// <param name="IpAddress">Null when the host didn't report one.</param>
/// <param name="UserAgent">What the client sent, or null. Anyone can write this, so only show it as text.</param>
public sealed record SessionRecord(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string? IpAddress, string? UserAgent);

/// <summary>Who is signing in, as far as the request can say.</summary>
public sealed record ClientDetails(IPAddress? Address, string? UserAgent);
