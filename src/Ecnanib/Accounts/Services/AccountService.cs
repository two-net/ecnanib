using System.Security.Cryptography;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Ui.Abstractions;
using Ecnanib.Ui.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ecnanib.Accounts.Services;

/// <summary>
/// Registration, sign-in, password change, account deletion and disclaimer acceptance over the database
/// (FR-ACC-01..04, FR-ACC-08). Authentication events are logged with the user id only; a submitted email or password
/// never reaches a log or an exception message (FR-LOG-04, FR-LOG-05).
/// </summary>
internal sealed class AccountService : IAccountService
{
    private static readonly User DummyUser = new() { Email = string.Empty, NormalizedEmail = string.Empty, PasswordHash = string.Empty, SecurityStamp = string.Empty };

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IPasswordHasher<User> _hasher;
    private readonly SignInThrottle _throttle;
    private readonly SessionRevocations _revocations;
    private readonly IOptions<AccountsOptions> _options;
    private readonly IOptions<UiOptions> _ui;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountService> _logger;
    private readonly Lazy<string> _dummyHash;

    public AccountService(
        IDbContextFactory<EcnanibDbContext> factory,
        IPasswordHasher<User> hasher,
        SignInThrottle throttle,
        SessionRevocations revocations,
        IOptions<AccountsOptions> options,
        IOptions<UiOptions> ui,
        TimeProvider time,
        ILogger<AccountService> logger)
    {
        _factory = factory;
        _hasher = hasher;
        _throttle = throttle;
        _revocations = revocations;
        _options = options;
        _ui = ui;
        _time = time;
        _logger = logger;
        // Verified against when the account does not exist, so an unknown email costs the same as a wrong password.
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(DummyUser, NewStamp()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<RegistrationResult> RegisterAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken)
    {
        if (_throttle.RetryAfterForAddress(clientAddress) is { } retryAfter)
        {
            _logger.LogWarning("Registration throttled from {Address}", clientAddress ?? "unknown");
            return new RegistrationResult(RegistrationStatus.Throttled, RetryAfter: retryAfter);
        }

        _throttle.RecordAttempt(clientAddress);
        if (!EmailAddress.TryNormalize(email, out var address, out var normalized))
            return new RegistrationResult(RegistrationStatus.InvalidEmail);
        if (!PasswordPolicy.IsAcceptable(password, _options.Value.MinPasswordLength))
            return new RegistrationResult(RegistrationStatus.InvalidPassword);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Registration refused: email already registered");
            return new RegistrationResult(RegistrationStatus.EmailTaken);
        }

        var user = new User
        {
            Email = address,
            NormalizedEmail = normalized,
            PasswordHash = string.Empty,
            SecurityStamp = NewStamp(),
            // A new account starts from the Ui section's defaults (Appendix D) until the user chooses on the settings page (FR-UI-02).
            Theme = _ui.Value.Theme,
            ReducedMotion = _ui.Value.ReducedMotion,
            CreatedAt = _time.GetUtcNow(),
        };
        user.PasswordHash = _hasher.HashPassword(user, password!);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race on the unique index. The exception text names the email, so it is not logged (FR-LOG-05).
            _logger.LogInformation("Registration refused: email already registered");
            return new RegistrationResult(RegistrationStatus.EmailTaken);
        }

        _logger.LogInformation("User {UserId} registered", user.Id);
        return new RegistrationResult(RegistrationStatus.Succeeded, user);
    }

    public async Task<AuthenticationResult> AuthenticateAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken)
    {
        var wellFormed = EmailAddress.TryNormalize(email, out _, out var normalized);
        var key = wellFormed ? normalized : (email ?? string.Empty).Trim().ToUpperInvariant();
        if (_throttle.RetryAfter(key, clientAddress) is { } retryAfter)
        {
            _logger.LogWarning("Sign-in throttled from {Address}", clientAddress ?? "unknown");
            return new AuthenticationResult(AuthenticationStatus.Throttled, RetryAfter: retryAfter);
        }

        password ??= string.Empty;
        User? user = null;
        EcnanibDbContext? db = null;
        try
        {
            if (wellFormed)
            {
                db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken).ConfigureAwait(false);
            }

            if (user is null)
            {
                // Same hashing cost as a wrong password, so timing does not reveal whether the account exists (NFR-SEC-01).
                _hasher.VerifyHashedPassword(DummyUser, _dummyHash.Value, password);
                _throttle.RecordFailure(key, clientAddress);
                _logger.LogWarning("Sign-in failed: unknown account");
                return new AuthenticationResult(AuthenticationStatus.InvalidCredentials);
            }

            var verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            if (verification == PasswordVerificationResult.Failed)
            {
                _throttle.RecordFailure(key, clientAddress);
                _logger.LogWarning("Sign-in failed for user {UserId}: wrong password", user.Id);
                return new AuthenticationResult(AuthenticationStatus.InvalidCredentials);
            }

            if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = _hasher.HashPassword(user, password);
                await db!.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("User {UserId} password hash upgraded to the current parameters", user.Id);
            }

            _throttle.RecordSuccess(key, clientAddress);
            _logger.LogInformation("User {UserId} signed in", user.Id);
            return new AuthenticationResult(AuthenticationStatus.Succeeded, user);
        }
        finally
        {
            if (db is not null)
                await db.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string? currentPassword, string? newPassword, string? clientAddress, CancellationToken cancellationToken)
    {
        if (!PasswordPolicy.IsAcceptable(newPassword, _options.Value.MinPasswordLength))
            return new PasswordChangeResult(PasswordChangeStatus.InvalidNewPassword);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return new PasswordChangeResult(PasswordChangeStatus.UserNotFound);

        if (_throttle.RetryAfter(user.NormalizedEmail, clientAddress) is { } retryAfter)
        {
            _logger.LogWarning("Password change for user {UserId} throttled from {Address}", userId, clientAddress ?? "unknown");
            return new PasswordChangeResult(PasswordChangeStatus.Throttled, RetryAfter: retryAfter);
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            _throttle.RecordFailure(user.NormalizedEmail, clientAddress);
            _logger.LogWarning("Password change for user {UserId} rejected: wrong current password", userId);
            return new PasswordChangeResult(PasswordChangeStatus.InvalidCurrentPassword);
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword!);
        user.SecurityStamp = NewStamp();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _revocations.Revoke(userId);
        _logger.LogInformation("User {UserId} changed their password; other sessions are ended", userId);
        return new PasswordChangeResult(PasswordChangeStatus.Succeeded, user);
    }

    public async Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, string? password, string? clientAddress, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return new AccountDeletionResult(AccountDeletionStatus.UserNotFound);

        if (_throttle.RetryAfter(user.NormalizedEmail, clientAddress) is { } retryAfter)
        {
            _logger.LogWarning("Account deletion for user {UserId} throttled from {Address}", userId, clientAddress ?? "unknown");
            return new AccountDeletionResult(AccountDeletionStatus.Throttled, RetryAfter: retryAfter);
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            _throttle.RecordFailure(user.NormalizedEmail, clientAddress);
            _logger.LogWarning("Account deletion for user {UserId} rejected: wrong password", userId);
            return new AccountDeletionResult(AccountDeletionStatus.InvalidPassword);
        }

        // Database:RetryOnFailureCount enables the retrying strategy; a transaction of our own must run inside it.
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);

            // Delete what FR-ACC-04 allows first. The row locks are held until commit, so an agent started concurrently
            // either committed before the count below (deletion refused) or finds its row gone (its update matches nothing).
            await db.Agents
                .Where(a => a.UserId == userId && (a.State == AgentState.Stopped || a.State == AgentState.Draft))
                .ExecuteDeleteAsync(token)
                .ConfigureAwait(false);
            var blocking = await db.Agents.CountAsync(a => a.UserId == userId, token).ConfigureAwait(false);
            if (blocking > 0)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return new AccountDeletionResult(AccountDeletionStatus.AgentsNotStopped, blocking);
            }

            // log_entries has no foreign keys (FR-LOG-08); every other user-owned row cascades from the user row.
            var logs = await db.LogEntries.Where(l => l.UserId == userId).ExecuteDeleteAsync(token).ConfigureAwait(false);
            var users = await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return users == 0
                ? new AccountDeletionResult(AccountDeletionStatus.UserNotFound)
                : new AccountDeletionResult(AccountDeletionStatus.Succeeded, 0, logs);
        }, cancellationToken).ConfigureAwait(false);

        if (result.Status == AccountDeletionStatus.Succeeded)
        {
            _revocations.Revoke(userId);
            _logger.LogInformation("User {UserId} deleted their account; {LogEntries} log entries removed", userId, result.DeletedLogEntries);
        }
        else if (result.Status == AccountDeletionStatus.AgentsNotStopped)
        {
            _logger.LogInformation("Account deletion for user {UserId} refused: {Agents} agent(s) not Stopped or Draft", userId, result.BlockingAgents);
        }

        return result;
    }

    public async Task<DisclaimerStatus> GetDisclaimerStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDisclaimerStatusAsync(db, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DisclaimerStatus> AcceptDisclaimerAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogWarning("Disclaimer acceptance by unknown user {UserId} ignored", userId);
            return new DisclaimerStatus(RiskDisclaimer.Version, null, null);
        }

        var now = _time.GetUtcNow();
        db.DisclaimerAcceptances.Add(new DisclaimerAcceptance { UserId = userId, Version = RiskDisclaimer.Version, AcceptedAt = now });
        user.DisclaimerAcceptedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("User {UserId} accepted risk disclaimer {Version}", userId, RiskDisclaimer.Version);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Already accepted this version: the first acceptance and its timestamp stand.
            db.ChangeTracker.Clear();
        }

        return await ReadDisclaimerStatusAsync(db, userId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DisclaimerStatus> ReadDisclaimerStatusAsync(EcnanibDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var last = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.DisclaimerAcceptedAt)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await db.DisclaimerAcceptances.AsNoTracking()
            .Where(a => a.UserId == userId && a.Version == RiskDisclaimer.Version)
            .Select(a => (DateTimeOffset?)a.AcceptedAt)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return new DisclaimerStatus(RiskDisclaimer.Version, current, last);
    }

    public async Task<UserPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserPreferences(u.Theme, u.ReducedMotion))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> UpdatePreferencesAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Theme, preferences.Theme)
                .SetProperty(u => u.ReducedMotion, preferences.ReducedMotion), cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            _logger.LogWarning("Preference change by unknown user {UserId} ignored", userId);
            return false;
        }

        _logger.LogInformation("User {UserId} chose the {Theme} theme with reduced motion {ReducedMotion}", userId, preferences.Theme, preferences.ReducedMotion);
        return true;
    }

    /// <summary>32 hex characters; <c>users.security_stamp</c> allows 64.</summary>
    internal static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
