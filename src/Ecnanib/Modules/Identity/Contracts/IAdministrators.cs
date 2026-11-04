namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// FR-AUTH-10: an administrator is an account whose verified address is in the configuration file.
/// Nothing stores the answer, so removing an address and restarting takes effect at once. With email
/// disabled nothing can verify an address, so the operator's Admin:BootstrapSecret does it instead.
/// See docs/architecture.md.
/// </summary>
public interface IAdministrators
{
    Task<bool> IsAdministratorAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether this user has an address only the bootstrap secret can verify.</summary>
    Task<bool> CanConfirmAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Verifies the user's address if they can confirm and the secret is right.</summary>
    Task<bool> ConfirmAsync(Guid userId, string secret, CancellationToken cancellationToken);

    /// <summary>Every account that's an administrator now, such as for notifications only they get (FR-NTF-01).</summary>
    Task<IReadOnlyList<Guid>> ListAsync(CancellationToken cancellationToken);
}
