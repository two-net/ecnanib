using System.Security.Cryptography;
using System.Text;
using Ecnanib.Accounts;
using Ecnanib.AiProviderGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Credentials;

/// <summary>Stores, replaces, and deletes the keys of the user who asks (FR-KEY-2, FR-KEY-10). See docs/credentials.md.</summary>
/// <remarks>
/// <para>
/// A key is write-only (FR-KEY-8). It comes in with a form, is encrypted, and is stored. Nothing
/// here reads it back: what a page shows is read from the columns beside it. The key is in no
/// log event and in no error, and the audit trail names it by the id of its row.
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
            .Select(credential => new { credential.Provider, credential.MaskedSuffix, credential.SavedAt })
            .ToListAsync(cancellationToken);

        var exchangeKeys = await context.Set<ExchangeCredential>()
            .OwnedBy(userId)
            .Select(credential => new { credential.Environment, credential.MaskedSuffix, credential.SavedAt })
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
                        ? new StoredKey(key.MaskedSuffix, key.SavedAt)
                        : null)),
            ],
            [
                .. Enum.GetValues<TradingMode>().Select(environment => new ExchangeKeySlot(
                    environment,
                    exchangeKeys.SingleOrDefault(key => key.Environment == environment) is { } key
                        ? new StoredKey(key.MaskedSuffix, key.SavedAt)
                        : null)),
            ]);
    }

    public async Task<StoreKeyResult> StoreAiKeyAsync(HttpContext request, AiProviderCode provider, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Asked first: nothing is looked at for a user who may not store a key.
        if (await RefusalAsync(context, request, userId, cancellationToken) is { } refusal)
        {
            return new StoreKeyResult(refusal);
        }

        if (!await context.Set<AiProvider>().AnyAsync(known => known.Code == provider, cancellationToken))
        {
            return new StoreKeyResult(StoreKeyOutcome.NoSuchProvider);
        }

        var key = apiKey.Trim();
        if (KeyRules.ProblemOf(key) is { } problem)
        {
            return new StoreKeyResult(StoreKeyOutcome.NotAKey, ApiKey: problem);
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
                new AiCredential(id, userId, provider, encrypted.Value, encrypted.KeyId, suffix, now));
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // In place: the row keeps its id, and the key that was stored is gone with this statement.
            await context.Set<AiCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Id == id)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(credential => credential.EncryptedValue, encrypted.Value)
                        .SetProperty(credential => credential.MasterKeyId, encrypted.KeyId)
                        .SetProperty(credential => credential.MaskedSuffix, suffix)
                        .SetProperty(credential => credential.SavedAt, now),
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

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Asked first, as for a key of an AI provider.
        if (await RefusalAsync(context, request, userId, cancellationToken) is { } refusal)
        {
            return new StoreKeyResult(refusal);
        }

        var key = apiKey.Trim();
        var secret = secretKey.Trim();
        var keyProblem = KeyRules.ProblemOf(key);
        var secretProblem = KeyRules.ProblemOf(secret);
        if (keyProblem is not null || secretProblem is not null)
        {
            return new StoreKeyResult(StoreKeyOutcome.NotAKey, keyProblem, secretProblem);
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
                new ExchangeCredential(id, userId, environment, encrypted.Value, encrypted.KeyId, suffix, now));
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
                        .SetProperty(credential => credential.SavedAt, now),
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

    /// <summary>
    /// What keeps the user of a request from storing a key, or null when nothing does. It is
    /// asked inside the transaction that stores the key, and locks the row of the account first,
    /// so the answer holds until the key is stored.
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
    /// <returns>Whether the address of the account is verified, or null when the account does not exist.</returns>
    private static async Task<bool?> LockAccountAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
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
public sealed record ExportedAiKey(Guid Id, string Provider, string EndsWith, DateTimeOffset SavedAt);

/// <summary>A Binance credential in the export of a user's data: which one it is, and never its keys (FR-KEY-8).</summary>
/// <param name="Id">The id that the audit trail names the credential by.</param>
/// <param name="Environment">The environment the credential is for.</param>
/// <param name="ApiKeyEndsWith">The last four characters of the API key.</param>
/// <param name="SavedAt">When it was stored.</param>
public sealed record ExportedExchangeKey(Guid Id, TradingMode Environment, string ApiKeyEndsWith, DateTimeOffset SavedAt);

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
            .Select(credential => new { credential.Id, credential.Provider, credential.MaskedSuffix, credential.SavedAt })
            .ToListAsync(cancellationToken);

        return [.. keys.Select(key => new ExportedAiKey(key.Id, key.Provider.Value, key.MaskedSuffix, key.SavedAt))];
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
            .Select(credential => new { credential.Id, credential.Environment, credential.MaskedSuffix, credential.SavedAt })
            .ToListAsync(cancellationToken);

        return [.. keys.Select(key => new ExportedExchangeKey(key.Id, key.Environment, key.MaskedSuffix, key.SavedAt))];
    }
}
