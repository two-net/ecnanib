using System.Text;
using Ecnanib.Database.Entities;
using Ecnanib.Database.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Database;

/// <summary>
/// The start-up step behind <see cref="DatabaseServiceCollectionExtensions.ProtectStoredKeysAsync"/> (FR-KEY-04,
/// FR-CFG-05): proves <c>Database:EncryptionKey</c> against the check value kept in <c>system_keys</c> (created on the
/// first start), re-encrypts every stored key when that value opens only under <c>Database:PreviousEncryptionKey</c>
/// (a rotation), and encrypts every row the <c>ProtectStoredKeys</c> migration left plain. One transaction, so a
/// failure leaves every byte as it was. Logs carry key ids and counts, never a value or a key.
/// </summary>
internal sealed class StoredKeyProtection
{
    internal const string Purpose = "Ecnanib.Database.StoredKeyProtection";

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly StoredKeyProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<StoredKeyProtection> _logger;

    public StoredKeyProtection(IDbContextFactory<EcnanibDbContext> factory, StoredKeyProtector protector, TimeProvider time, ILogger<StoredKeyProtection> logger)
    {
        _factory = factory;
        _protector = protector;
        _time = time;
        _logger = logger;
    }

    private enum Mode
    {
        /// <summary>No check value yet: the first start under this key; every encrypted row must already open under it.</summary>
        Create,
        /// <summary>The check value opens under the current key.</summary>
        Current,
        /// <summary>The check value opens under the previous key only: every encrypted row moves to the current one.</summary>
        Rotate,
    }

    public async Task ProtectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            // Database:RetryOnFailureCount enables the retrying strategy, so the transaction runs inside it; a retry runs
            // the whole delegate again over a cleared tracker.
            var strategy = db.Database.CreateExecutionStrategy();
            var (mode, encrypted, rotated) = await strategy.ExecuteAsync(async token =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                var check = await db.SystemKeys.SingleOrDefaultAsync(k => k.Purpose == Purpose, token).ConfigureAwait(false);
                var mode = Decide(check);
                // A normal start touches the plain rows only; a rotation or a missing check value reads every encrypted row too.
                var exchangeKeys = await (mode == Mode.Current ? db.ExchangeKeys.Where(k => k.Cipher == StoredKeyCipher.Plain) : db.ExchangeKeys)
                    .ToListAsync(token).ConfigureAwait(false);
                var providerKeys = await (mode == Mode.Current ? db.ProviderKeys.Where(k => k.Cipher == StoredKeyCipher.Plain) : db.ProviderKeys)
                    .ToListAsync(token).ConfigureAwait(false);

                var encrypted = 0;
                var rotated = 0;
                foreach (var key in exchangeKeys)
                {
                    switch (key.Cipher)
                    {
                        case StoredKeyCipher.Plain:
                            _protector.Protect(key, Plain(key.ProtectedApiKey), Plain(key.ProtectedApiSecret));
                            encrypted++;
                            break;
                        case StoredKeyCipher.AesGcm when mode == Mode.Rotate:
                            _protector.Protect(key,
                                Previous(StoredKeyColumn.ExchangeApiKey, key.Id, key.ProtectedApiKey, "Exchange"),
                                Previous(StoredKeyColumn.ExchangeApiSecret, key.Id, key.ProtectedApiSecret, "Exchange"));
                            rotated++;
                            break;
                        case StoredKeyCipher.AesGcm when mode == Mode.Create:
                            Prove(() => _protector.Unprotect(key), key.Id, "Exchange");
                            break;
                    }
                }

                foreach (var key in providerKeys)
                {
                    switch (key.Cipher)
                    {
                        case StoredKeyCipher.Plain:
                            _protector.Protect(key, Plain(key.ProtectedApiKey));
                            encrypted++;
                            break;
                        case StoredKeyCipher.AesGcm when mode == Mode.Rotate:
                            _protector.Protect(key, Previous(StoredKeyColumn.ProviderApiKey, key.Id, key.ProtectedApiKey, "Provider"));
                            rotated++;
                            break;
                        case StoredKeyCipher.AesGcm when mode == Mode.Create:
                            Prove(() => _protector.Unprotect(key), key.Id, "Provider");
                            break;
                    }
                }

                var value = Convert.ToBase64String(_protector.ProtectCheckValue(Purpose));
                if (mode == Mode.Create)
                    db.SystemKeys.Add(new SystemKey { Purpose = Purpose, Value = value, CreatedAt = _time.GetUtcNow() });
                else if (mode == Mode.Rotate)
                    check!.Value = value;

                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return (mode, encrypted, rotated);
            }, cancellationToken).ConfigureAwait(false);

            switch (mode)
            {
                case Mode.Create:
                    _logger.LogInformation("Stored key protection check value created (first start under this Database:EncryptionKey, FR-CFG-05)");
                    break;
                case Mode.Rotate:
                    _logger.LogWarning("Database:EncryptionKey changed; {Rows} stored key row(s) and the check value were re-encrypted under it. Remove Database:PreviousEncryptionKey", rotated);
                    break;
                case Mode.Current when _protector.HasPreviousKey:
                    _logger.LogWarning("Database:PreviousEncryptionKey is set, but the stored keys already open under Database:EncryptionKey; remove it");
                    break;
            }

            _logger.LogInformation("Stored keys protected: {Encrypted} plain row(s) encrypted (FR-KEY-04)", encrypted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var wrapped = ex as StoredKeyProtectionException
                ?? new StoredKeyProtectionException($"Stored key protection failed: {DatabaseMigrator.Describe(ex)}", ex);
            // The message carries every cause (Describe); no exception on the line, so the console shows no stack trace (FR-CFG-06).
            _logger.LogCritical("Stored key protection failed; the application will not start: {Reason}", wrapped.Message);
            throw wrapped;
        }
    }

    private Mode Decide(SystemKey? check)
    {
        if (check is null)
            return Mode.Create;

        byte[] stored;
        try
        {
            stored = Convert.FromBase64String(check.Value);
        }
        catch (FormatException ex)
        {
            throw new StoredKeyProtectionException($"The check value in system_keys ({Purpose}) is not base64; the row was changed by hand.", ex);
        }

        if (_protector.CheckValueOpens(Purpose, stored, withPrevious: false))
            return Mode.Current;
        if (_protector.CheckValueOpens(Purpose, stored, withPrevious: true))
            return Mode.Rotate;
        throw new StoredKeyProtectionException(
            "The stored API keys were encrypted under a key that is neither Database:EncryptionKey nor Database:PreviousEncryptionKey; " +
            "this instance will not start. Restore the key the database was encrypted with, or set it as Database:PreviousEncryptionKey " +
            "beside the new one for one start to rotate.");
    }

    /// <summary>A row the ProtectStoredKeys migration converted: its bytes are the text an earlier version stored.</summary>
    private static string Plain(byte[] stored) => Encoding.UTF8.GetString(stored);

    private string Previous(StoredKeyColumn column, Guid keyId, byte[] stored, string kind)
    {
        try
        {
            return _protector.UnprotectWithPrevious(column, keyId, stored);
        }
        catch (StoredKeyProtectionException ex)
        {
            throw new StoredKeyProtectionException($"{kind} key {keyId} does not open under Database:PreviousEncryptionKey; nothing was rotated. Delete the row or supply the key it was encrypted with.", ex);
        }
    }

    private static void Prove(Func<object> unprotect, Guid keyId, string kind)
    {
        try
        {
            unprotect();
        }
        catch (StoredKeyProtectionException ex)
        {
            throw new StoredKeyProtectionException($"{kind} key {keyId} was encrypted under another key than Database:EncryptionKey and the check value is missing from system_keys; restore the key the database was encrypted with, or set it as Database:PreviousEncryptionKey beside the new one for one start to rotate.", ex);
        }
    }
}
