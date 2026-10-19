using System.Security.Claims;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Authentication;

internal enum SessionCheck
{
    /// <summary>The stamp was read again and still matches; the ticket should be renewed.</summary>
    Kept,

    /// <summary>The ticket is recent and not revoked, so no lookup was made.</summary>
    KeptWithoutLookup,

    /// <summary>Malformed ticket, account gone or security stamp changed: end the session.</summary>
    Rejected,
}

/// <summary>
/// Decides whether a session principal is still valid (FR-ACC-02..04, NFR-SEC-01): the security stamp claim is compared
/// with the account after <c>Accounts:SessionRevalidationMinutes</c>, or at once when the user was revoked since the
/// ticket was issued. Shared by the cookie handler and the interactive circuit provider.
/// </summary>
internal sealed class SessionValidator
{
    private readonly ISecurityStampSource _stamps;
    private readonly SessionRevocations _revocations;
    private readonly IOptions<AccountsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionValidator> _logger;

    public SessionValidator(ISecurityStampSource stamps, SessionRevocations revocations, IOptions<AccountsOptions> options, TimeProvider time, ILogger<SessionValidator> logger)
    {
        _stamps = stamps;
        _revocations = revocations;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <param name="issuedUtc">When the ticket was issued; null forces a lookup.</param>
    public async Task<SessionCheck> CheckAsync(ClaimsPrincipal? principal, DateTimeOffset? issuedUtc, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var stamp = principal.GetSecurityStamp();
        if (userId is null || string.IsNullOrEmpty(stamp))
        {
            _logger.LogInformation("Session rejected: malformed ticket");
            return SessionCheck.Rejected;
        }

        if (issuedUtc is { } issued
            && _time.GetUtcNow() - issued < _options.Value.SessionRevalidation
            && !_revocations.IsRevokedSince(userId.Value, issued))
            return SessionCheck.KeptWithoutLookup;

        var current = await _stamps.FindAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        if (current is not null && string.Equals(current, stamp, StringComparison.Ordinal))
            return SessionCheck.Kept;

        _logger.LogInformation("Session of user {UserId} rejected: {Reason}", userId, current is null ? "account no longer exists" : "security stamp changed");
        return SessionCheck.Rejected;
    }
}
