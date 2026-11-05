using Ecnanib.Modules.Credentials.Contracts;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// Tells every <see cref="ICredentialObserver"/> about a committed change: Catalog and Agents. The observers are
/// resolved on the first change rather than injected, because an observer naturally uses the contracts it
/// observes: Agents reads <see cref="ICredentials"/> to decide which agents to pause, and injecting the
/// observers into <see cref="Credentials"/> would make that a cycle that stops startup.
/// </summary>
internal sealed class CredentialObservers(IServiceProvider services, ILogger<CredentialObservers> logger)
{
    private readonly Lazy<IReadOnlyList<ICredentialObserver>> _observers = new(() => [.. services.GetServices<ICredentialObserver>()]);

    /// <summary>
    /// Called once the change has committed, so an observer never hears of one that rolled back. Observers
    /// must return at once and never throw, but one that does can't undo a committed change, or stop the others.
    /// </summary>
    public void Notify(Credential row, CredentialChangeKind change, CredentialStatus? status, CredentialStatus? previous)
    {
        var notice = new CredentialChange(row.UserId, row.Id, row.Kind, row.Provider, change, status, previous);

        foreach (var observer in _observers.Value)
        {
            try
            {
                observer.OnChanged(notice);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception, "Telling {Observer} that credential {CredentialId} was {Change} failed", observer.GetType().Name, row.Id, change);
            }
        }
    }
}
