using System.Security.Cryptography;
using System.Text;
using Ecnanib.Accounts;
using Ecnanib.AiProviderGateway;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Credentials;

/// <summary>
/// Stores, replaces, and deletes the keys of the user who asks (FR-KEY-2, FR-KEY-10), and stores
/// only a key that its provider takes (FR-KEY-3, FR-KEY-4). See docs/credentials.md.
/// </summary>
/// <remarks>
/// <para>
/// A key is write-only (FR-KEY-8). It comes in with a form, is shown to its provider, is
/// encrypted, and is stored. Nothing here reads it back: what a page shows is read from the
/// columns beside it. The key is in no log event and in no error, and the audit trail names it
/// by the id of its row.
/// </para>
/// <para>
/// The provider is asked about a key before it is stored, with a call that only reads. A
/// provider can take long to answer, so it is asked with no transaction open, between two of
/// them: the first says whether the user may store a key at all, so that no call goes out for
/// one who may not, and the second asks that again, under the lock, and stores the key.
/// </para>
/// <para>
/// Every change starts by locking the row of the account, as the changes to an account do. So
/// two forms of one user are taken one after the other, and the second finds what the first left:
/// a form that is sent twice stores one key, and the second time replaces it.
/// </para>
/// <para>
/// A key is stored only for a user who has just proved again who they are (FR-ACC-4), and whose
/// address is verified. Both are asked under that lock.
/// </para>
/// </remarks>
internal sealed class StoredKeys(
    IDbContextFactory<EcnanibDbContext> contexts,
    StoredSecrets secrets,
    IReauthentication reauthentication,
    IAiKeyChecks aiProviders,
    IExchangeAdapter exchange,
    IKeyValidation validation,
    IEnumerable<IKeyDependents> dependents,
    TimeProvider clock,
    ILogger<StoredKeys> logger) : IStoredKeys
{
    public async Task<StoredKeysState> StateAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The providers are the rows of their table, and not a list of this code: a provider
        // that is added there has a place here.
        var providers = await context.Set<AiProvider>()
            .OrderBy(provider => provider.Code)
            .ToListAsync(cancellationToken);

        // Only what is shown. The encrypted keys are not read for a page at all.
        var aiKeys = await context.Set<AiCredential>()
            .OwnedBy(userId)
            .Select(credential => new
            {
                credential.Provider,
                credential.MaskedSuffix,
                credential.SavedAt,
                credential.Status,
                credential.ValidatedAt,
            })
            .ToListAsync(cancellationToken);

        var exchangeKeys = await context.Set<ExchangeCredential>()
            .OwnedBy(userId)
            .Select(credential => new
            {
                credential.Environment,
                credential.MaskedSuffix,
                credential.SavedAt,
                credential.Status,
                credential.ValidatedAt,
                credential.SpotTrading,
                credential.FuturesTrading,
                credential.IpRestricted,
            })
            .ToListAsync(cancellationToken);

        // An account that was deleted since the request was signed in is not verified.
        var addressVerified = await context.Set<User>()
            .AnyAsync(user => user.Id == userId && user.EmailVerifiedAt != null, cancellationToken);

        return new StoredKeysState(
            addressVerified,
            [
                .. providers.Select(provider => new AiKeySlot(
                    provider.Code,
                    provider.DisplayName,
                    aiKeys.SingleOrDefault(key => key.Provider == provider.Code) is { } key
                        ? new StoredKey(key.MaskedSuffix, key.SavedAt, key.Status, key.ValidatedAt)
                        : null)),
            ],
            [
                .. Enum.GetValues<TradingMode>().Select(environment =>
                    exchangeKeys.SingleOrDefault(key => key.Environment == environment) is { } key
                        ? new ExchangeKeySlot(
                            environment,
                            new StoredKey(key.MaskedSuffix, key.SavedAt, key.Status, key.ValidatedAt),
                            new ExchangePermissions(key.SpotTrading, key.FuturesTrading, key.IpRestricted))
                        : new ExchangeKeySlot(environment, Key: null)),
            ]);
    }

    public async Task<StoreKeyResult> StoreAiKeyAsync(HttpContext request, AiProviderCode provider, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        // Asked first, in a transaction of its own, which ends before the provider is asked:
        // nothing is looked at, and no call goes out, for a user who may not store a key.
        await using (var asking = await contexts.CreateDbContextAsync(cancellationToken))
        {
            await using var first = await asking.Database.BeginTransactionAsync(cancellationToken);

            if (await RefusalAsync(asking, request, userId, cancellationToken) is { } refusal)
            {
                return new StoreKeyResult(refusal);
            }

            if (!await asking.Set<AiProvider>().AnyAsync(known => known.Code == provider, cancellationToken))
            {
                return new StoreKeyResult(StoreKeyOutcome.NoSuchProvider);
            }
        }

        var key = apiKey.Trim();
        if (KeyRules.ProblemOf(key) is { } problem)
        {
            return new StoreKeyResult(StoreKeyOutcome.NotAKey, ApiKey: problem);
        }

        // The provider says whether it takes the key, with a call that only reads (FR-KEY-3).
        // Only a key that it takes is stored.
        switch (await aiProviders.CheckAsync(provider, key, cancellationToken))
        {
            case KeyVerdict.Accepted:
                break;

            case KeyVerdict.Refused:
                logger.LogInformation(
                    "User {UserId} was refused storing a key of the AI provider {Provider}: the provider did not take it",
                    userId,
                    provider.Value);

                return new StoreKeyResult(StoreKeyOutcome.Refused);

            default:
                logger.LogInformation(
                    "User {UserId} could not store a key of the AI provider {Provider}: the provider gave no answer",
                    userId,
                    provider.Value);

                return new StoreKeyResult(StoreKeyOutcome.Unreachable);
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Asked again, now under the lock that holds until the key is stored: the proof may
        // have ended, or the account may have gone, while the provider was asked.
        if (await RefusalAsync(context, request, userId, cancellationToken) is { } late)
        {
            return new StoreKeyResult(late);
        }

        var now = clock.GetUtcNow();
        var suffix = KeyRules.LastCharactersOf(key);

        // Read under the lock, so that this is what the user has stored until the transaction ends.
        var storedId = await context.Set<AiCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Provider == provider)
            .Select(credential => (Guid?)credential.Id)
            .SingleOrDefaultAsync(cancellationToken);

        var id = storedId ?? Guid.CreateVersion7();
        var encrypted = Encrypt(Encoding.UTF8.GetBytes(key), AiCredential.BoundTo(id, userId));

        if (storedId is null)
        {
            context.Set<AiCredential>().Add(
                new AiCredential(id, userId, provider, encrypted.Value, encrypted.KeyId, suffix, now, KeyStatus.Valid, now));
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // In place: the row keeps its id, and the key that was stored is gone with this
            // statement, with what was found about it.
            await context.Set<AiCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Id == id)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(credential => credential.EncryptedValue, encrypted.Value)
                        .SetProperty(credential => credential.MasterKeyId, encrypted.KeyId)
                        .SetProperty(credential => credential.MaskedSuffix, suffix)
                        .SetProperty(credential => credential.SavedAt, now)
                        .SetProperty(credential => credential.Status, KeyStatus.Valid)
                        .SetProperty(credential => credential.ValidatedAt, now),
                    cancellationToken);
        }

        await AuditTrail.RecordAsync(
            context,
            userId,
            storedId is null ? AuditAction.KeyAdded : AuditAction.KeyReplaced,
            AuditTarget.AiKey(id),
            now,
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // The provider, and never the key or anything of it.
        if (storedId is null)
        {
            logger.LogInformation("User {UserId} added a key of the AI provider {Provider}", userId, provider.Value);
        }
        else
        {
            logger.LogInformation("User {UserId} replaced their key of the AI provider {Provider}", userId, provider.Value);
        }

        return new StoreKeyResult(StoreKeyOutcome.Stored);
    }

    public async Task<StoreKeyResult> StoreExchangeKeyAsync(
        HttpContext request, TradingMode environment, string apiKey, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        // Asked first, as for a key of an AI provider, and ended before Binance is asked.
        await using (var asking = await contexts.CreateDbContextAsync(cancellationToken))
        {
            await using var first = await asking.Database.BeginTransactionAsync(cancellationToken);

            if (await RefusalAsync(asking, request, userId, cancellationToken) is { } refusal)
            {
                return new StoreKeyResult(refusal);
            }
        }

        var key = apiKey.Trim();
        var secret = secretKey.Trim();
        var keyProblem = KeyRules.ProblemOf(key);
        var secretProblem = KeyRules.ProblemOf(secret);
        if (keyProblem is not null || secretProblem is not null)
        {
            return new StoreKeyResult(StoreKeyOutcome.NotAKey, keyProblem, secretProblem);
        }

        // Binance says whether it takes the credential and what the credential may do, with
        // calls that only read (FR-KEY-3). Only a credential that it takes is stored, and never
        // one that can withdraw (FR-KEY-4, NFR-LEG-2).
        var check = await exchange.CheckKeyAsync(environment, key, secret, cancellationToken);
        switch (check.Verdict)
        {
            case KeyVerdict.Accepted when check.CanWithdraw == true:
                logger.LogInformation(
                    "User {UserId} was refused storing a key of Binance for {TradingMode}: Binance reports that it can withdraw",
                    userId,
                    environment);

                return new StoreKeyResult(StoreKeyOutcome.CanWithdraw);

            case KeyVerdict.Accepted:
                break;

            case KeyVerdict.Refused:
                logger.LogInformation(
                    "User {UserId} was refused storing a key of Binance for {TradingMode}: Binance did not take it", userId, environment);

                return new StoreKeyResult(StoreKeyOutcome.Refused);

            default:
                logger.LogInformation(
                    "User {UserId} could not store a key of Binance for {TradingMode}: Binance gave no answer", userId, environment);

                return new StoreKeyResult(StoreKeyOutcome.Unreachable);
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Asked again, under the lock that holds until the credential is stored.
        if (await RefusalAsync(context, request, userId, cancellationToken) is { } late)
        {
            return new StoreKeyResult(late);
        }

        var now = clock.GetUtcNow();
        var suffix = KeyRules.LastCharactersOf(key);

        var storedId = await context.Set<ExchangeCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Environment == environment)
            .Select(credential => (Guid?)credential.Id)
            .SingleOrDefaultAsync(cancellationToken);

        var id = storedId ?? Guid.CreateVersion7();
        var encrypted = Encrypt(ExchangeCredential.ValueOf(key, secret), ExchangeCredential.BoundTo(id, userId));

        if (storedId is null)
        {
            context.Set<ExchangeCredential>().Add(
                new ExchangeCredential(
                    id,
                    userId,
                    environment,
                    encrypted.Value,
                    encrypted.KeyId,
                    suffix,
                    now,
                    KeyStatus.Valid,
                    now,
                    check.SpotTrading,
                    check.FuturesTrading,
                    check.IpRestricted));
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await context.Set<ExchangeCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Id == id)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(credential => credential.EncryptedValue, encrypted.Value)
                        .SetProperty(credential => credential.MasterKeyId, encrypted.KeyId)
                        .SetProperty(credential => credential.MaskedSuffix, suffix)
                        .SetProperty(credential => credential.SavedAt, now)
                        .SetProperty(credential => credential.Status, KeyStatus.Valid)
                        .SetProperty(credential => credential.ValidatedAt, now)
                        .SetProperty(credential => credential.SpotTrading, check.SpotTrading)
                        .SetProperty(credential => credential.FuturesTrading, check.FuturesTrading)
                        .SetProperty(credential => credential.IpRestricted, check.IpRestricted),
                    cancellationToken);
        }

        await AuditTrail.RecordAsync(
            context,
            userId,
            storedId is null ? AuditAction.KeyAdded : AuditAction.KeyReplaced,
            AuditTarget.ExchangeKey(id),
            now,
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        if (storedId is null)
        {
            logger.LogInformation("User {UserId} added a key of Binance for {TradingMode}", userId, environment);
        }
        else
        {
            logger.LogInformation("User {UserId} replaced their key of Binance for {TradingMode}", userId, environment);
        }

        return new StoreKeyResult(StoreKeyOutcome.Stored);
    }

    public async Task DeleteAiKeyAsync(HttpContext request, AiProviderCode provider)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await LockAccountAsync(context, userId, cancellationToken) is null)
        {
            // The account was deleted, on another browser, since this request was signed in.
            return;
        }

        var storedId = await context.Set<AiCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Provider == provider)
            .Select(credential => (Guid?)credential.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (storedId is not { } id)
        {
            // Nothing is stored: the form was sent twice, or sent from two browsers. It is what
            // was asked for.
            return;
        }

        // What depends on the key is told while the key is still there, in this transaction: an
        // agent is not left running on a key that is gone (FR-KEY-10).
        foreach (var dependent in dependents)
        {
            await dependent.AiKeyDeletedAsync(context, userId, provider, cancellationToken);
        }

        await context.Set<AiCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        await AuditTrail.RecordAsync(
            context,
            userId,
            AuditAction.KeyDeleted,
            AuditTarget.AiKey(id),
            clock.GetUtcNow(),
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} deleted their key of the AI provider {Provider}", userId, provider.Value);
    }

    public async Task DeleteExchangeKeyAsync(HttpContext request, TradingMode environment)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await LockAccountAsync(context, userId, cancellationToken) is null)
        {
            // The account was deleted, on another browser, since this request was signed in.
            return;
        }

        var storedId = await context.Set<ExchangeCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Environment == environment)
            .Select(credential => (Guid?)credential.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (storedId is not { } id)
        {
            return;
        }

        foreach (var dependent in dependents)
        {
            await dependent.ExchangeKeyDeletedAsync(context, userId, environment, cancellationToken);
        }

        await context.Set<ExchangeCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        await AuditTrail.RecordAsync(
            context,
            userId,
            AuditAction.KeyDeleted,
            AuditTarget.ExchangeKey(id),
            clock.GetUtcNow(),
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} deleted their key of Binance for {TradingMode}", userId, environment);
    }

    public Task<KeyCheckOutcome> CheckAiKeyAsync(HttpContext request, AiProviderCode provider) =>
        validation.RevalidateAiKeyAsync(SignedInUser.RequiredIdOf(request), provider, request.RequestAborted);

    public Task<KeyCheckOutcome> CheckExchangeKeyAsync(HttpContext request, TradingMode environment) =>
        validation.RevalidateExchangeKeyAsync(SignedInUser.RequiredIdOf(request), environment, request.RequestAborted);

    /// <summary>
    /// What keeps the user of a request from storing a key, or null when nothing does. It is
    /// asked inside a transaction, and locks the row of the account first, so the answer holds
    /// until that transaction ends: the one that stores the key, and before it the one that says
    /// whether the provider is asked at all.
    /// </summary>
    private async Task<StoreKeyOutcome?> RefusalAsync(
        EcnanibDbContext context, HttpContext request, Guid userId, CancellationToken cancellationToken)
    {
        if (await LockAccountAsync(context, userId, cancellationToken) is not { } addressVerified)
        {
            // The account was deleted, on another browser, since this request was signed in.
            // Nobody is signed in on this one any more.
            return StoreKeyOutcome.ConfirmationNeeded;
        }

        // On the server, in the request that brings the key, and under the lock: a new password
        // ends the session that holds the proof, and changes the row, so it came before this or
        // waits for it (FR-ACC-4).
        if (!await reauthentication.IsRecentAsync(context, request, SensitiveAction.AddOrReplaceKey))
        {
            return StoreKeyOutcome.ConfirmationNeeded;
        }

        // An address that is not verified may have been registered by someone it does not
        // belong to. Its owner can still take the account over by registering it again, and
        // would take over a key that was stored in it: one that is not theirs, or one of someone
        // who mistyped their address.
        if (!addressVerified)
        {
            logger.LogInformation("User {UserId} was refused storing a key: their email address is not verified", userId);

            return StoreKeyOutcome.AddressNotVerified;
        }

        return null;
    }

    /// <summary>
    /// Locks the row of an account for the rest of the transaction, as the first thing the
    /// transaction asks of it and with the lock of a change. The deletion of the account takes
    /// the stronger lock of a deletion, so it and a change to the keys happen one after the other.
    /// </summary>
    /// <remarks>
    /// Whatever changes a key of a user takes this lock first, the check of a stored key
    /// included, so that they all wait for each other in one order.
    /// </remarks>
    /// <returns>Whether the address of the account is verified, or null when the account does not exist.</returns>
    internal static async Task<bool?> LockAccountAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var locked = await context.Database
            .SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM users
                WHERE id = {userId}
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return null;
        }

        // Read after the lock, so that this is what the row holds until the transaction ends.
        return await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => user.EmailVerifiedAt != null)
            .SingleAsync(cancellationToken);
    }

    /// <summary>Encrypts a key with the active master key, and clears what was handed in (FR-KEY-9).</summary>
    private EncryptedSecret Encrypt(byte[] key, string boundTo)
    {
        try
        {
            return secrets.Encrypt(key, boundTo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>A key of an AI provider in the export of a user's data: which one it is, and never the key (FR-KEY-8).</summary>
/// <param name="Id">The id that the audit trail names the key by.</param>
/// <param name="Provider">The code of the provider the key is for.</param>
/// <param name="EndsWith">The last four characters of the key.</param>
/// <param name="SavedAt">When it was stored.</param>
/// <param name="Status">What the last check of it found.</param>
/// <param name="ValidatedAt">When the provider last answered a check of it, or null when it never did.</param>
public sealed record ExportedAiKey(
    Guid Id, string Provider, string EndsWith, DateTimeOffset SavedAt, KeyStatus Status, DateTimeOffset? ValidatedAt);

/// <summary>A Binance credential in the export of a user's data: which one it is, and never its keys (FR-KEY-8).</summary>
/// <param name="Id">The id that the audit trail names the credential by.</param>
/// <param name="Environment">The environment the credential is for.</param>
/// <param name="ApiKeyEndsWith">The last four characters of the API key.</param>
/// <param name="SavedAt">When it was stored.</param>
/// <param name="Status">What the last check of it found.</param>
/// <param name="ValidatedAt">When Binance last answered a check of it, or null when it never did.</param>
/// <param name="SpotTrading">Whether it may trade on the Spot market, as Binance last said.</param>
/// <param name="FuturesTrading">Whether it may trade on the Futures market.</param>
/// <param name="IpRestricted">Whether it works only from listed addresses, or null where Binance does not report it.</param>
public sealed record ExportedExchangeKey(
    Guid Id,
    TradingMode Environment,
    string ApiKeyEndsWith,
    DateTimeOffset SavedAt,
    KeyStatus Status,
    DateTimeOffset? ValidatedAt,
    bool SpotTrading,
    bool FuturesTrading,
    bool? IpRestricted);

/// <summary>The keys of the AI providers that a user stored, for the export of their data (NFR-SEC-9).</summary>
internal sealed class AiKeysExport : IExportedPart
{
    public string Section => "aiKeys";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        // Only what a page shows. The encrypted keys are never read for an export.
        var keys = await context.Set<AiCredential>()
            .OwnedBy(userId)
            .OrderBy(credential => credential.Provider)
            .Select(credential => new
            {
                credential.Id,
                credential.Provider,
                credential.MaskedSuffix,
                credential.SavedAt,
                credential.Status,
                credential.ValidatedAt,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. keys.Select(key => new ExportedAiKey(
                key.Id, key.Provider.Value, key.MaskedSuffix, key.SavedAt, key.Status, key.ValidatedAt)),
        ];
    }
}

/// <summary>The Binance credentials that a user stored, for the export of their data (NFR-SEC-9).</summary>
internal sealed class ExchangeKeysExport : IExportedPart
{
    public string Section => "exchangeKeys";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var keys = await context.Set<ExchangeCredential>()
            .OwnedBy(userId)
            .OrderBy(credential => credential.Environment)
            .Select(credential => new
            {
                credential.Id,
                credential.Environment,
                credential.MaskedSuffix,
                credential.SavedAt,
                credential.Status,
                credential.ValidatedAt,
                credential.SpotTrading,
                credential.FuturesTrading,
                credential.IpRestricted,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. keys.Select(key => new ExportedExchangeKey(
                key.Id,
                key.Environment,
                key.MaskedSuffix,
                key.SavedAt,
                key.Status,
                key.ValidatedAt,
                key.SpotTrading,
                key.FuturesTrading,
                key.IpRestricted)),
        ];
    }
}
