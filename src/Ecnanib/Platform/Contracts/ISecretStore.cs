using System.Security.Cryptography;
using Ecnanib.Kernel;

namespace Ecnanib.Platform.Contracts;

/// <summary>
/// Secrets at rest (FR-KEY-07, FR-KEY-09), such as user API keys and TOTP secrets. Each is encrypted
/// with the active master key and stored by ID, and its owner keeps the ID. Only Identity and
/// Credentials may use this, which BannedApis enforces, because administrators must never see a
/// decrypted secret (FR-ADM-01). See Secrets in docs/architecture.md.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Encrypts <paramref name="value"/> with the active key and adds it to <paramref name="context"/>
    /// without saving, so the caller's save commits it with the row that refers to it. The caller
    /// still owns the value, and disposes it.
    /// </summary>
    /// <returns>The ID to store, which is all that refers to the secret.</returns>
    Guid Add(EcnanibDbContext context, SecretValue value);

    /// <summary>
    /// Deletes a secret straight away, so call it inside the caller's transaction, with the change
    /// that stops referring to it. An ID that doesn't exist is ignored. Nothing else knows when a
    /// secret is no longer used, so its owner must delete it, including when an account is deleted.
    /// </summary>
    Task RemoveAsync(EcnanibDbContext context, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Decrypts a secret in memory. Use it at once and dispose it. Never cache it, or keep it anywhere
    /// that outlives the operation that needed it (FR-KEY-09).
    /// </summary>
    /// <exception cref="KeyNotFoundException">There's no secret with this ID.</exception>
    /// <exception cref="CryptographicException">
    /// It can't be decrypted: its key isn't configured, or the stored bytes were changed. The message
    /// holds the secret's ID and key ID, and nothing else.
    /// </exception>
    Task<SecretValue> RevealAsync(Guid id, CancellationToken cancellationToken);
}
