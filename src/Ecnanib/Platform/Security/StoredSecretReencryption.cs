using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Security;

/// <summary>Moves the stored secrets to the active master key when the application starts.</summary>
public static class StoredSecretReencryption
{
    /// <summary>
    /// Encrypts every stored secret that another master key than the active one encrypted again,
    /// with the active one (FR-KEY-7). After that, an earlier master key encrypts nothing, and
    /// can be taken out of the configuration file. Call it after the database has been migrated
    /// and before the host starts: the active key changes only with a start, so this is when
    /// there is something to do. See docs/security.md.
    /// </summary>
    /// <remarks>
    /// It does not keep the application from starting. A secret that could not be encrypted again
    /// is still read with the master key it names, and is tried again at the next start.
    /// </remarks>
    public static void ReencryptStoredSecrets(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        host.Services.GetRequiredService<StoredSecretReencryptor>().Run();
    }
}

/// <summary>
/// Goes through every column that a component declared to hold a stored secret, and encrypts the
/// values that do not name the active master key again.
/// </summary>
/// <remarks>
/// <para>
/// A value is written only where the row still holds the value that was read. So this is safe
/// beside a second instance that starts at the same time, and beside one that still serves
/// requests: neither overwrites what the other wrote.
/// </para>
/// <para>
/// This is synchronous for the same reason as the migrations. It runs once, on the thread that
/// starts the application, before anything else does.
/// </para>
/// </remarks>
internal sealed class StoredSecretReencryptor(
    IEnumerable<StoredSecretColumn> columns,
    StoredSecrets secrets,
    IDbContextFactory<EcnanibDbContext> contexts,
    ILogger<StoredSecretReencryptor> logger)
{
    public void Run()
    {
        try
        {
            using var reading = contexts.CreateDbContext();
            using var writing = contexts.CreateDbContext();

            var left = 0;
            foreach (var column in columns)
            {
                Report(column.NameIn(reading.Model), column.MoveToTheActiveKey(reading, writing, secrets));

                // Asked of the database, and not added up from the above: what counts is what
                // the column holds now.
                left += column.CountUnderOtherKeys(reading, secrets);
            }

            if (left > 0)
            {
                logger.LogWarning(
                    "{ValueCount} stored secrets are still encrypted with another master key than the active one, {ActiveKeyId}. " +
                    "Keep the master keys they name in the configuration file",
                    left,
                    secrets.ActiveKeyId);

                return;
            }

            logger.LogInformation("Every stored secret is encrypted with the active master key {ActiveKeyId}", secrets.ActiveKeyId);

            var others = secrets.KeyIds.Where(keyId => keyId != secrets.ActiveKeyId).Order(StringComparer.Ordinal).ToList();
            if (others.Count > 0)
            {
                logger.LogInformation(
                    "No stored secret is encrypted with the master keys {KeyIds}, so they can be taken out of the configuration file",
                    others);
            }
        }
        catch (Exception error)
        {
            // The database went away, most likely. The application can do everything without
            // this: what was not encrypted again is still read with the master key it names.
            logger.LogError(
                error,
                "The stored secrets could not all be encrypted again with the active master key {ActiveKeyId}. Those that were " +
                "not are still read with the master keys they name, and are tried again at the next start",
                secrets.ActiveKeyId);
        }
    }

    private void Report(string column, MovedSecrets result)
    {
        if (result.Moved > 0)
        {
            logger.LogInformation(
                "Encrypted {ValueCount} stored secrets of {Column} again, with the active master key {ActiveKeyId}",
                result.Moved,
                column,
                secrets.ActiveKeyId);
        }

        foreach (var (keyId, count) in result.NamingAnUnlistedKey.OrderBy(counted => counted.Key, StringComparer.Ordinal))
        {
            logger.LogError(
                "{ValueCount} stored secrets of {Column} name the master key {KeyId}, which the configuration file does not list. " +
                "They cannot be read until it lists that key again",
                count,
                column,
                keyId);
        }

        foreach (var (keyId, count) in result.NotDecrypting.OrderBy(counted => counted.Key, StringComparer.Ordinal))
        {
            logger.LogError(
                "{ValueCount} stored secrets of {Column} do not decrypt with the master key {KeyId}, which they name. They were " +
                "changed, or the configuration file lists another key under that id. They are left as they are",
                count,
                column,
                keyId);
        }
    }
}
