using System.Collections.Concurrent;
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
/// Checks a stored key again: asks its provider, or Binance, about it with a call that only
/// reads, and records what it said (FR-KEY-3). It is what the daily job, a call that failed
/// authentication, and the button of the page all come to. See docs/credentials.md.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that decrypts a stored key. The key is read for the one call that asks
/// about it, and the bytes are cleared when the call is made (FR-KEY-9). It is in no log event
/// and in no error: an event names the user, and the provider or the environment.
/// </para>
/// <para>
/// The provider is asked with no transaction open. What it said is then written only where the
/// row still holds the value that was read, so a key that was replaced meanwhile never gets what
/// was said about the one before it.
/// </para>
/// <para>
/// A write that tells <see cref="IKeyDependents"/> locks the row of the account first, as every
/// change to a user's keys does, then tells them, and then changes the row of the key. A user
/// who deletes the key at that moment holds the same lock, so the two wait for each other in
/// one order.
/// </para>
/// </remarks>
internal sealed class KeyValidator(
    IDbContextFactory<EcnanibDbContext> contexts,
    StoredSecrets secrets,
    IAiKeyChecks aiProviders,
    IExchangeAdapter exchange,
    IEnumerable<IKeyDependents> dependents,
    TimeProvider clock,
    ILogger<KeyValidator> logger) : IKeyValidation
{
    /// <summary>How long after a check a key is checked again by the job: keys are validated daily (FR-KEY-3).</summary>
    public static readonly TimeSpan Daily = TimeSpan.FromDays(1);

    /// <summary>
    /// The shortest time between two checks of one key. A provider that gives no answer leaves
    /// no time in the row, so the attempts are counted here, in memory: a button that is pressed
    /// again and again, or a call that fails again and again, asks the provider once a minute.
    /// </summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    // When each key was last asked about, by the id of its row. An entry is of no use after the
    // pause, and is dropped when there are many.
    private const int ManyAttempts = 4096;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _attempts = new();

    public async Task<KeyCheckOutcome> RevalidateAiKeyAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken)
    {
        // Only what the check needs: which row it is, and the value to read the key from.
        StoredValue? stored;
        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            stored = await context.Set<AiCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Provider == provider)
                .Select(credential => new StoredValue(credential.Id, credential.EncryptedValue, credential.MasterKeyId))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (stored is null)
        {
            return KeyCheckOutcome.NoKey;
        }

        if (!MayAsk(stored.Id))
        {
            return KeyCheckOutcome.TooSoon;
        }

        if (Read(stored, AiCredential.BoundTo(stored.Id, userId)) is not { } key)
        {
            logger.LogError(
                "The key of user {UserId} for the AI provider {Provider} cannot be read, so it is not checked", userId, provider.Value);

            return KeyCheckOutcome.Unreadable;
        }

        var verdict = await aiProviders.CheckAsync(provider, key, cancellationToken);
        var now = clock.GetUtcNow();

        switch (verdict)
        {
            case KeyVerdict.Accepted:
            {
                await using var context = await contexts.CreateDbContextAsync(cancellationToken);

                var written = await Holding(context.Set<AiCredential>(), stored).ExecuteUpdateAsync(
                    row => row
                        .SetProperty(credential => credential.Status, KeyStatus.Valid)
                        .SetProperty(credential => credential.ValidatedAt, now),
                    cancellationToken);

                return written == 0 ? KeyCheckOutcome.Changed : KeyCheckOutcome.Valid;
            }

            case KeyVerdict.Refused:
            {
                await using var context = await contexts.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                if (await StoredKeys.LockAccountAsync(context, userId, cancellationToken) is null)
                {
                    return KeyCheckOutcome.NoKey;
                }

                var status = await Holding(context.Set<AiCredential>(), stored)
                    .Select(credential => (KeyStatus?)credential.Status)
                    .SingleOrDefaultAsync(cancellationToken);

                if (status is null)
                {
                    return KeyCheckOutcome.Changed;
                }

                // Told once, when the key becomes invalid, and before it is marked: an agent is
                // not left running on a key that its provider refuses (FR-KEY-10).
                if (status != KeyStatus.Invalid)
                {
                    foreach (var dependent in dependents)
                    {
                        await dependent.AiKeyInvalidAsync(context, userId, provider, cancellationToken);
                    }
                }

                await Holding(context.Set<AiCredential>(), stored).ExecuteUpdateAsync(
                    row => row
                        .SetProperty(credential => credential.Status, KeyStatus.Invalid)
                        .SetProperty(credential => credential.ValidatedAt, now),
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "The key of user {UserId} for the AI provider {Provider} was refused by the provider, and is invalid",
                    userId,
                    provider.Value);

                return KeyCheckOutcome.Invalid;
            }

            default:
                logger.LogInformation(
                    "The key of user {UserId} for the AI provider {Provider} was not checked: the provider gave no answer. It is checked again later",
                    userId,
                    provider.Value);

                return KeyCheckOutcome.Unreachable;
        }
    }

    public async Task<KeyCheckOutcome> RevalidateExchangeKeyAsync(Guid userId, TradingMode environment, CancellationToken cancellationToken)
    {
        StoredValue? stored;
        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            stored = await context.Set<ExchangeCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Environment == environment)
                .Select(credential => new StoredValue(credential.Id, credential.EncryptedValue, credential.MasterKeyId))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (stored is null)
        {
            return KeyCheckOutcome.NoKey;
        }

        if (!MayAsk(stored.Id))
        {
            return KeyCheckOutcome.TooSoon;
        }

        // The two keys are stored as one value: the API key, a line feed, and the secret key.
        var keys = Read(stored, ExchangeCredential.BoundTo(stored.Id, userId));
        var parting = keys?.IndexOf('\n', StringComparison.Ordinal) ?? -1;
        if (keys is null || parting <= 0)
        {
            logger.LogError(
                "The key of user {UserId} of Binance for {TradingMode} cannot be read, so it is not checked", userId, environment);

            return KeyCheckOutcome.Unreadable;
        }

        var check = await exchange.CheckKeyAsync(environment, keys[..parting], keys[(parting + 1)..], cancellationToken);
        var now = clock.GetUtcNow();

        switch (check.Verdict)
        {
            // A key that can withdraw is not kept, whenever it turns out to be one (FR-KEY-4, NFR-LEG-2).
            case KeyVerdict.Accepted when check.CanWithdraw == true:
            {
                await using var context = await contexts.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                if (await StoredKeys.LockAccountAsync(context, userId, cancellationToken) is null)
                {
                    return KeyCheckOutcome.NoKey;
                }

                if (!await Holding(context.Set<ExchangeCredential>(), stored).AnyAsync(cancellationToken))
                {
                    return KeyCheckOutcome.Changed;
                }

                // As when the user deletes it: what depends on the key is told while it is
                // still there, in this transaction.
                foreach (var dependent in dependents)
                {
                    await dependent.ExchangeKeyDeletedAsync(context, userId, environment, cancellationToken);
                }

                await Holding(context.Set<ExchangeCredential>(), stored).ExecuteDeleteAsync(cancellationToken);

                // The service did it, for the owner of the key: no request is at hand.
                await AuditTrail.RecordAsync(
                    context,
                    userId,
                    AuditAction.KeyRejected,
                    AuditTarget.ExchangeKey(stored.Id),
                    now,
                    AuditOrigin.Unknown,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "The key of user {UserId} of Binance for {TradingMode} can withdraw, as Binance reports, and was deleted",
                    userId,
                    environment);

                return KeyCheckOutcome.Deleted;
            }

            case KeyVerdict.Accepted:
            {
                await using var context = await contexts.CreateDbContextAsync(cancellationToken);

                var written = await Holding(context.Set<ExchangeCredential>(), stored).ExecuteUpdateAsync(
                    row => row
                        .SetProperty(credential => credential.Status, KeyStatus.Valid)
                        .SetProperty(credential => credential.ValidatedAt, now)
                        .SetProperty(credential => credential.SpotTrading, check.SpotTrading)
                        .SetProperty(credential => credential.FuturesTrading, check.FuturesTrading)
                        .SetProperty(credential => credential.IpRestricted, check.IpRestricted),
                    cancellationToken);

                return written == 0 ? KeyCheckOutcome.Changed : KeyCheckOutcome.Valid;
            }

            case KeyVerdict.Refused:
            {
                await using var context = await contexts.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                if (await StoredKeys.LockAccountAsync(context, userId, cancellationToken) is null)
                {
                    return KeyCheckOutcome.NoKey;
                }

                var status = await Holding(context.Set<ExchangeCredential>(), stored)
                    .Select(credential => (KeyStatus?)credential.Status)
                    .SingleOrDefaultAsync(cancellationToken);

                if (status is null)
                {
                    return KeyCheckOutcome.Changed;
                }

                if (status != KeyStatus.Invalid)
                {
                    foreach (var dependent in dependents)
                    {
                        await dependent.ExchangeKeyInvalidAsync(context, userId, environment, cancellationToken);
                    }
                }

                // What the key may do stays as Binance last said it: a refusal says nothing about it.
                await Holding(context.Set<ExchangeCredential>(), stored).ExecuteUpdateAsync(
                    row => row
                        .SetProperty(credential => credential.Status, KeyStatus.Invalid)
                        .SetProperty(credential => credential.ValidatedAt, now),
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "The key of user {UserId} of Binance for {TradingMode} was refused by Binance, and is invalid", userId, environment);

                return KeyCheckOutcome.Invalid;
            }

            default:
                logger.LogInformation(
                    "The key of user {UserId} of Binance for {TradingMode} was not checked: Binance gave no answer. It is checked again later",
                    userId,
                    environment);

                return KeyCheckOutcome.Unreachable;
        }
    }

    public async Task<StartBlock?> BlockForAsync(Guid userId, TradingMode environment, Market market, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var key = await context.Set<ExchangeCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Environment == environment)
            .Select(credential => new { credential.Status, credential.SpotTrading, credential.FuturesTrading })
            .SingleOrDefaultAsync(cancellationToken);

        return key switch
        {
            null => StartBlock.NoKey,
            { Status: not KeyStatus.Valid } => StartBlock.NotValid,
            _ => market switch
            {
                Market.Spot => key.SpotTrading ? null : StartBlock.SpotTradingMissing,
                Market.Futures => key.FuturesTrading ? null : StartBlock.FuturesTradingMissing,
                _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
            },
        };
    }

    /// <summary>The row of a key, while it still holds the value that was read: not a key that took its place.</summary>
    private static IQueryable<AiCredential> Holding(IQueryable<AiCredential> rows, StoredValue stored) =>
        rows.Where(credential => credential.Id == stored.Id && credential.EncryptedValue == stored.EncryptedValue);

    private static IQueryable<ExchangeCredential> Holding(IQueryable<ExchangeCredential> rows, StoredValue stored) =>
        rows.Where(credential => credential.Id == stored.Id && credential.EncryptedValue == stored.EncryptedValue);

    /// <summary>Whether a key may be asked about now, which it may once in <see cref="Pause"/>. Asking counts from here.</summary>
    private bool MayAsk(Guid id)
    {
        var now = clock.GetUtcNow();

        if (_attempts.Count > ManyAttempts)
        {
            foreach (var (key, asked) in _attempts)
            {
                if (now - asked >= Pause)
                {
                    _attempts.TryRemove(new KeyValuePair<Guid, DateTimeOffset>(key, asked));
                }
            }
        }

        while (true)
        {
            if (!_attempts.TryGetValue(id, out var last))
            {
                if (_attempts.TryAdd(id, now))
                {
                    return true;
                }
            }
            else if (now - last < Pause && now >= last)
            {
                return false;
            }
            else if (_attempts.TryUpdate(id, now, last))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// A stored key as text, for the one call that asks about it, or null when the value does
    /// not decrypt. The bytes are cleared here (FR-KEY-9).
    /// </summary>
    private string? Read(StoredValue stored, string boundTo)
    {
        byte[] key;
        try
        {
            key = secrets.Decrypt(new EncryptedSecret(stored.MasterKeyId, stored.EncryptedValue), boundTo);
        }
        catch (CryptographicException)
        {
            // It names a master key that the file does not list, or was changed. The start of
            // the application has said so, for every such value.
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>What a check reads of a row: which one it is, and the encrypted key.</summary>
    private sealed record StoredValue(Guid Id, byte[] EncryptedValue, string MasterKeyId);
}
