namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>NFR-SEC-02. Each call does slow, memory-hard work, so run it only where a limit applies.</summary>
public interface IPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken cancellationToken);

    Task<bool> VerifyAsync(string password, string hash, CancellationToken cancellationToken);

    /// <summary>
    /// Costs what verifying costs and checks nothing, for a refusal that mustn't come back any quicker,
    /// because the time taken would show that the account doesn't exist or is locked.
    /// </summary>
    Task SpendAsync(string password, CancellationToken cancellationToken);
}
