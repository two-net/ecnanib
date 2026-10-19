using System.Collections.Concurrent;
using Ecnanib.Accounts.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Authentication;

/// <summary>
/// Users whose sessions must be re-checked at once: a password change or an account deletion revokes every ticket issued
/// before it (FR-ACC-03, FR-ACC-04) instead of waiting for the revalidation interval. In memory, which the single running
/// instance allows (C-10); after a restart the interval alone bounds the gap. Entries older than a session lifetime are dropped.
/// </summary>
internal sealed class SessionRevocations
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _revokedAt = new();
    private readonly IOptions<AccountsOptions> _options;
    private readonly TimeProvider _time;

    public SessionRevocations(IOptions<AccountsOptions> options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    internal int Count => _revokedAt.Count;

    public void Revoke(Guid userId)
    {
        var now = _time.GetUtcNow();
        _revokedAt[userId] = now;
        var cutoff = now - _options.Value.SessionLifetime;
        foreach (var (id, at) in _revokedAt)
        {
            if (at < cutoff)
                _revokedAt.TryRemove(KeyValuePair.Create(id, at));
        }
    }

    /// <summary>True when a ticket issued at <paramref name="issuedUtc"/> predates a revocation of this user.</summary>
    public bool IsRevokedSince(Guid userId, DateTimeOffset issuedUtc) =>
        _revokedAt.TryGetValue(userId, out var at) && at >= issuedUtc;
}
