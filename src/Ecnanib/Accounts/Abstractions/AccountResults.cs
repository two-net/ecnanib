using Ecnanib.Database.Entities;

namespace Ecnanib.Accounts.Abstractions;

// User mistakes are results, never exceptions: the pages turn them into messages. Exceptions mean infrastructure failed.

public enum RegistrationStatus
{
    Succeeded,
    InvalidEmail,
    InvalidPassword,
    EmailTaken,
    Throttled,
}

public sealed record RegistrationResult(RegistrationStatus Status, User? User = null, TimeSpan? RetryAfter = null);

public enum AuthenticationStatus
{
    Succeeded,
    /// <summary>Unknown email or wrong password; the two are never told apart (NFR-SEC-01).</summary>
    InvalidCredentials,
    Throttled,
}

public sealed record AuthenticationResult(AuthenticationStatus Status, User? User = null, TimeSpan? RetryAfter = null);

public enum PasswordChangeStatus
{
    Succeeded,
    InvalidCurrentPassword,
    InvalidNewPassword,
    Throttled,
    UserNotFound,
}

public sealed record PasswordChangeResult(PasswordChangeStatus Status, User? User = null, TimeSpan? RetryAfter = null);

public enum AccountDeletionStatus
{
    Succeeded,
    InvalidPassword,
    /// <summary>At least one agent is not Stopped or Draft (FR-ACC-04); nothing was deleted.</summary>
    AgentsNotStopped,
    Throttled,
    UserNotFound,
}

public sealed record AccountDeletionResult(AccountDeletionStatus Status, int BlockingAgents = 0, int DeletedLogEntries = 0, TimeSpan? RetryAfter = null);

/// <summary>
/// Whether the user has accepted the current <see cref="RiskDisclaimer.Version"/> (FR-ACC-08). <see cref="LastAcceptedAt"/>
/// is the latest acceptance of any version, so a page can say "changed since you last accepted".
/// </summary>
public sealed record DisclaimerStatus(string CurrentVersion, DateTimeOffset? CurrentVersionAcceptedAt, DateTimeOffset? LastAcceptedAt)
{
    public bool IsAccepted => CurrentVersionAcceptedAt is not null;
}

/// <summary>One fair-use decision (FR-ACC-07): the limit's name, whether one more is allowed, and the counts behind it.</summary>
public sealed record FairUseCheck(string Limit, bool Allowed, int Current, int Max);
