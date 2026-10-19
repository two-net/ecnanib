namespace Ecnanib.Accounts.Authentication;

/// <summary>Reads a user's current security stamp; null when the account no longer exists.</summary>
internal interface ISecurityStampSource
{
    Task<string?> FindAsync(Guid userId, CancellationToken cancellationToken);
}
