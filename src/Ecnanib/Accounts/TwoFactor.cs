using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>Turns two-factor authentication on and off, and replaces recovery codes. See docs/accounts.md.</summary>
/// <remarks>
/// <para>
/// Nothing is stored for an account until the first code is accepted. The key of a setup travels
/// in the form, encrypted for the user it was made for. If a key were stored when the page is
/// opened, someone who once held a session could open the page, note the key, and wait for the
/// owner to confirm it. For the same reason the key of a page can be confirmed for
/// <see cref="TwoFactorRules.SetupLifetime"/> and no longer: what is sealed is the key and the
/// time the page was made.
/// </para>
/// <para>
/// Each change is one transaction that starts by locking the account's row, so that it and a
/// sign-in of the same account happen one after the other.
/// </para>
/// </remarks>
internal sealed class TwoFactor(
    ApplicationSettings application,
    IDbContextFactory<EcnanibDbContext> contexts,
    StoredSecrets secrets,
    PasswordHasher passwords,
    TwoFactorCodes codes,
    TimeProvider clock,
    ILogger<TwoFactor> logger) : ITwoFactor
{
    // Longer than any key the application seals, with room for the id of a master key.
    private const int LongestSealedKey = 512;

    public async Task<TwoFactorState> StateAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);

        var account = await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                user.TwoFactorEnabledAt,
                user.EmailVerifiedAt,
                // The codes of this account. Inside a projection, so it names the owner itself.
                RecoveryCodes = context.Set<RecoveryCode>().Count(code => code.UserId == user.Id),
            })
            .SingleAsync(request.RequestAborted);

        return new TwoFactorState(
            IsOn: account.TwoFactorEnabledAt is not null,
            Since: account.TwoFactorEnabledAt,
            RecoveryCodesLeft: account.RecoveryCodes,
            AddressVerified: account.EmailVerifiedAt is not null);
    }

    public TwoFactorSetup NewSetup(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);

        // The key, and after it the time it was made at, in seconds.
        Span<byte> setup = stackalloc byte[Totp.KeyLength + sizeof(long)];
        var key = setup[..Totp.KeyLength];
        try
        {
            RandomNumberGenerator.Fill(key);
            BinaryPrimitives.WriteInt64BigEndian(setup[Totp.KeyLength..], clock.GetUtcNow().ToUnixTimeSeconds());

            var stored = secrets.Encrypt(setup, SetupBoundTo(userId));

            return SetupFor(request, key, $"{stored.KeyId}.{Base64Url.EncodeToString(stored.Value)}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(setup);
        }
    }

    public TwoFactorSetup? SetupOf(HttpContext request, string sealedKey)
    {
        ArgumentNullException.ThrowIfNull(sealedKey);

        if (Unseal(SignedInUser.RequiredIdOf(request), sealedKey, clock.GetUtcNow()) is not { } key)
        {
            return null;
        }

        try
        {
            return SetupFor(request, key, sealedKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<TwoFactorTurnOn> TurnOnAsync(HttpContext request, string sealedKey, string password, string code)
    {
        ArgumentNullException.ThrowIfNull(sealedKey);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(code);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var account = await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => new { user.PasswordHash, user.EmailVerifiedAt, user.TwoFactorEnabledAt })
            .SingleAsync(cancellationToken);

        // Before anything is looked at, so that a form which is sent a second time, by a reload
        // of the page that showed the recovery codes, gets this answer and not an error.
        if (account.TwoFactorEnabledAt is not null)
        {
            return Ended(TwoFactorTurnOnOutcome.AlreadyOn);
        }

        // An address that is not verified may have been registered by someone it does not
        // belong to. If they could turn this on, the owner of the address, who can still take
        // the account over by registering it again, would be locked out of it for good.
        if (account.EmailVerifiedAt is null)
        {
            return Ended(TwoFactorTurnOnOutcome.AddressNotVerified);
        }

        if (Unseal(userId, sealedKey, now) is not { } key)
        {
            return Ended(TwoFactorTurnOnOutcome.KeyNotValid);
        }

        EncryptedSecret stored;
        long? step;
        try
        {
            var typed = TwoFactorCodes.WithoutSpaces(code);
            step = TwoFactorCodes.LooksLikeAppCode(typed)
                ? TwoFactorCodes.StepOf(key, typed, now, lastStep: null, out _)
                : null;

            stored = secrets.Encrypt(key, TwoFactorCodes.BoundTo(userId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        // The code before the password: it costs nothing to check, and a password costs a hash.
        if (step is null)
        {
            return Ended(TwoFactorTurnOnOutcome.WrongCode);
        }

        // The password too, so that someone at a browser that was left signed in cannot enroll
        // an app of their own and keep the account from its owner.
        if (password.Length == 0
            || PasswordRules.ProblemOf(password) == PasswordProblem.TooLong
            || !await passwords.VerifyAsync(password, account.PasswordHash))
        {
            return Ended(TwoFactorTurnOnOutcome.WrongPassword);
        }

        var recoveryCodes = NewRecoveryCodes(userId, now);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // One statement, which is also the lock on the row, and only while everything that was
        // checked still holds: of two requests, one turns it on and the other finds it on. The
        // step of the code is stored with the key, so the code is not accepted again to sign in.
        var turnedOn = await context.Set<User>()
            .Where(user => user.Id == userId
                && user.TwoFactorEnabledAt == null
                && user.EmailVerifiedAt != null
                && user.PasswordHash == account.PasswordHash)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(user => user.TwoFactorSecret, (byte[]?)stored.Value)
                    .SetProperty(user => user.TwoFactorSecretKeyId, (string?)stored.KeyId)
                    .SetProperty(user => user.TwoFactorEnabledAt, (DateTimeOffset?)now)
                    .SetProperty(user => user.TwoFactorLastStep, step)
                    .SetProperty(user => user.TwoFactorFailedCodes, 0)
                    .SetProperty(user => user.TwoFactorLockedUntil, (DateTimeOffset?)null),
                cancellationToken);

        if (turnedOn != 1)
        {
            var onNow = await context.Set<User>()
                .AnyAsync(user => user.Id == userId && user.TwoFactorEnabledAt != null, cancellationToken);

            // Another request turned it on, or the password was changed while it was verified.
            return Ended(onNow ? TwoFactorTurnOnOutcome.AlreadyOn : TwoFactorTurnOnOutcome.WrongPassword);
        }

        context.Set<RecoveryCode>().AddRange(recoveryCodes.Rows);
        await context.SaveChangesAsync(cancellationToken);

        // The other sessions were started with the password alone. So was a sign-in that waits
        // for a code of an earlier key, if there is one.
        var ended = await Sessions.EndOthersAsync(
            context, userId, SessionCookie.TokenOf(request.Request), cancellationToken);

        await AuditTrail.RecordAsync(
            context,
            userId,
            AuditAction.TwoFactorTurnedOn,
            AuditTrail.Account(userId),
            now,
            AuditTrail.OriginOf(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserId} turned two-factor authentication on, which ended {SessionCount} other sessions", userId, ended);

        return new TwoFactorTurnOn(TwoFactorTurnOnOutcome.TurnedOn, recoveryCodes.Texts);

        static TwoFactorTurnOn Ended(TwoFactorTurnOnOutcome outcome) => new(outcome, []);
    }

    public async Task<TwoFactorChangeOutcome> TurnOffAsync(HttpContext request, string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var userId = SignedInUser.RequiredIdOf(request);
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);
        await using var transaction = await context.Database.BeginTransactionAsync(request.RequestAborted);

        if (await TwoFactorCodes.LockAsync(context, userId, request.RequestAborted) is not { IsOn: true } account)
        {
            return TwoFactorChangeOutcome.NotOn;
        }

        var answer = await codes.JudgeAsync(context, account, code, AuditTrail.OriginOf(request), now);
        if (answer == CodeAnswer.Right)
        {
            // From here on nothing is canceled with the request: the code is spent.
            await context.Set<User>()
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(row => row
                    .SetProperty(user => user.TwoFactorSecret, (byte[]?)null)
                    .SetProperty(user => user.TwoFactorSecretKeyId, (string?)null)
                    .SetProperty(user => user.TwoFactorEnabledAt, (DateTimeOffset?)null)
                    .SetProperty(user => user.TwoFactorLastStep, (long?)null)
                    .SetProperty(user => user.TwoFactorFailedCodes, 0)
                    .SetProperty(user => user.TwoFactorLockedUntil, (DateTimeOffset?)null));

            await context.Set<RecoveryCode>()
                .OwnedBy(userId)
                .ExecuteDeleteAsync();

            // A sign-in that waits for a code would wait for one that no longer exists.
            await Sessions.EndWaitingAsync(context, userId, CancellationToken.None);

            await AuditTrail.RecordAsync(
                context,
                userId,
                AuditAction.TwoFactorTurnedOff,
                AuditTrail.Account(userId),
                now,
                AuditTrail.OriginOf(request),
                CancellationToken.None);
        }

        // Whatever the answer: a wrong code is counted in this transaction.
        await transaction.CommitAsync(CancellationToken.None);

        if (answer == CodeAnswer.Right)
        {
            logger.LogInformation("User {UserId} turned two-factor authentication off", userId);
        }

        return OutcomeOf(answer);
    }

    public async Task<RecoveryCodesReplacement> ReplaceRecoveryCodesAsync(HttpContext request, string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var userId = SignedInUser.RequiredIdOf(request);
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);
        await using var transaction = await context.Database.BeginTransactionAsync(request.RequestAborted);

        if (await TwoFactorCodes.LockAsync(context, userId, request.RequestAborted) is not { IsOn: true } account)
        {
            return new RecoveryCodesReplacement(TwoFactorChangeOutcome.NotOn, []);
        }

        IReadOnlyList<string> texts = [];

        var answer = await codes.JudgeAsync(context, account, code, AuditTrail.OriginOf(request), now);
        if (answer == CodeAnswer.Right)
        {
            // From here on nothing is canceled with the request: the code is spent.
            await context.Set<RecoveryCode>()
                .OwnedBy(userId)
                .ExecuteDeleteAsync();

            var recoveryCodes = NewRecoveryCodes(userId, now);
            context.Set<RecoveryCode>().AddRange(recoveryCodes.Rows);
            await context.SaveChangesAsync(CancellationToken.None);
            texts = recoveryCodes.Texts;

            await AuditTrail.RecordAsync(
                context,
                userId,
                AuditAction.TwoFactorRecoveryCodesReplaced,
                AuditTrail.Account(userId),
                now,
                AuditTrail.OriginOf(request),
                CancellationToken.None);
        }

        // Whatever the answer: a wrong code is counted in this transaction.
        await transaction.CommitAsync(CancellationToken.None);

        if (answer == CodeAnswer.Right)
        {
            logger.LogInformation("User {UserId} replaced their recovery codes", userId);
        }

        return new RecoveryCodesReplacement(OutcomeOf(answer), texts);
    }

    /// <summary>What the key of a setup is bound to, so that it is of use to the user it was made for only.</summary>
    private static string SetupBoundTo(Guid userId) => $"two-factor-setup:{userId}";

    private static TwoFactorChangeOutcome OutcomeOf(CodeAnswer answer) => answer switch
    {
        CodeAnswer.Right => TwoFactorChangeOutcome.Done,
        CodeAnswer.Refused => TwoFactorChangeOutcome.CodesRefused,
        _ => TwoFactorChangeOutcome.WrongCode,
    };

    private TwoFactorSetup SetupFor(HttpContext request, ReadOnlySpan<byte> key, string sealedKey)
    {
        // In groups of four, which is easier to type and to check.
        var text = Totp.TextOf(key);
        var grouped = string.Join(' ', Enumerable.Range(0, text.Length / 4).Select(group => text.Substring(group * 4, 4)));

        // The app shows the name of the service and of the account beside the code.
        var address = request.User.Identity?.Name ?? string.Empty;

        return new TwoFactorSetup(grouped, Totp.UriFor(application.Name, address, key), sealedKey);
    }

    /// <summary>
    /// The key that a form carried, or null when the text is not a key that was sealed for the
    /// user, or was sealed too long ago.
    /// </summary>
    private byte[]? Unseal(Guid userId, string sealedKey, DateTimeOffset now)
    {
        // The id of the master key, a dot, and the encrypted key. The text comes from a form,
        // as whoever sent the request wrote it.
        var dot = sealedKey.LastIndexOf('.');
        if (dot <= 0 || sealedKey.Length > LongestSealedKey)
        {
            return null;
        }

        byte[] setup;
        try
        {
            setup = secrets.Decrypt(
                new EncryptedSecret(sealedKey[..dot], Base64Url.DecodeFromChars(sealedKey.AsSpan(dot + 1))),
                SetupBoundTo(userId));
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return null;
        }

        try
        {
            if (setup.Length != Totp.KeyLength + sizeof(long))
            {
                return null;
            }

            var madeAt = BinaryPrimitives.ReadInt64BigEndian(setup.AsSpan(Totp.KeyLength));

            return now.ToUnixTimeSeconds() - madeAt < (long)TwoFactorRules.SetupLifetime.TotalSeconds
                ? setup[..Totp.KeyLength]
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(setup);
        }
    }

    private static (List<RecoveryCode> Rows, List<string> Texts) NewRecoveryCodes(Guid userId, DateTimeOffset now)
    {
        var rows = new List<RecoveryCode>();
        var texts = new List<string>();

        for (var number = 0; number < TwoFactorRules.RecoveryCodeCount; number++)
        {
            var (text, hash) = RecoveryCodes.Create();

            rows.Add(new RecoveryCode(userId, hash, now));
            texts.Add(text);
        }

        return (rows, texts);
    }
}
