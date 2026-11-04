using System.Security.Cryptography;
using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Secrets;

/// <summary>
/// Seals and opens rows in <c>secrets</c>. Each row's ID is bound into its ciphertext as associated
/// data, so ciphertext copied onto another row doesn't decrypt there. Plaintext is only ever held in
/// pinned buffers that are cleared after use, until it's handed over as a <see cref="SecretValue"/>.
/// </summary>
internal sealed class SecretStore(IDbContextFactory<EcnanibDbContext> contextFactory, MasterKeys keys) : ISecretStore
{
    private const string AssociatedDataPrefix = "ecnanib/secret/v1/";

    public Guid Add(EcnanibDbContext context, SecretValue value)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(value);

        var secret = Seal(Guid.CreateVersion7(), value.RevealUtf8());
        context.Set<Secret>().Add(secret);

        return secret.Id;
    }

    public Task RemoveAsync(EcnanibDbContext context, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Set<Secret>().Where(secret => secret.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<SecretValue> RevealAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return ToValue(id, await context.Set<Secret>().AsNoTracking().SingleOrDefaultAsync(secret => secret.Id == id, cancellationToken));
    }

    /// <summary>Adds and saves at once. For Data Protection, whose callbacks are synchronous.</summary>
    public Guid AddNow(ReadOnlySpan<byte> plaintext)
    {
        using var context = contextFactory.CreateDbContext();

        var secret = Seal(Guid.CreateVersion7(), plaintext);
        context.Set<Secret>().Add(secret);
        context.SaveChanges();

        return secret.Id;
    }

    /// <summary>For Data Protection, whose callbacks are synchronous.</summary>
    public SecretValue Reveal(Guid id)
    {
        using var context = contextFactory.CreateDbContext();

        return ToValue(id, context.Set<Secret>().AsNoTracking().SingleOrDefault(secret => secret.Id == id));
    }

    /// <summary>Seals <paramref name="plaintext"/> with the active key, bound to <paramref name="id"/>.</summary>
    public Secret Seal(Guid id, ReadOnlySpan<byte> plaintext) => new()
    {
        Id = id,
        KeyId = keys.Active.Id,
        Ciphertext = SecretCipher.Seal(keys.Active, plaintext, AssociatedData(id)),
    };

    /// <summary>Re-seals a tracked row with the active key, so that saving the context writes it.</summary>
    /// <exception cref="CryptographicException">The row can't be decrypted. It's left unchanged.</exception>
    public void Reseal(Secret secret)
    {
        var plaintext = Open(secret);

        try
        {
            secret.Ciphertext = SecretCipher.Seal(keys.Active, plaintext, AssociatedData(secret.Id));
            secret.KeyId = keys.Active.Id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>How many secrets each key seals, for startup and the health page.</summary>
    public static Task<Dictionary<string, long>> CountByKeyAsync(EcnanibDbContext context, CancellationToken cancellationToken) =>
        context.Set<Secret>()
            .GroupBy(secret => secret.KeyId)
            .Select(group => new { KeyId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(group => group.KeyId, group => group.Count, StringComparer.Ordinal, cancellationToken);

    /// <returns>A pinned buffer, which the caller clears.</returns>
    /// <exception cref="CryptographicException">The message holds the secret's ID and key ID, and nothing else.</exception>
    private byte[] Open(Secret secret)
    {
        if (!keys.TryGet(secret.KeyId, out var key))
        {
            throw new CryptographicException(
                $"Secret {secret.Id} was encrypted with key \"{secret.KeyId}\", which isn't configured.");
        }

        try
        {
            return SecretCipher.Open(key, secret.Ciphertext, AssociatedData(secret.Id));
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                $"Secret {secret.Id} couldn't be decrypted with key \"{secret.KeyId}\". Its stored bytes were changed, or it belongs to another row.",
                exception);
        }
    }

    private SecretValue ToValue(Guid id, Secret? secret)
    {
        if (secret is null)
        {
            throw new KeyNotFoundException($"There's no secret {id}.");
        }

        var plaintext = Open(secret);

        try
        {
            return SecretValue.FromUtf8(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(Guid id) => Encoding.ASCII.GetBytes($"{AssociatedDataPrefix}{id:D}");
}
