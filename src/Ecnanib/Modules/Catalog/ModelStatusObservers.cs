using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Tells every <see cref="IModelStatusObserver"/> about committed changes to models' statuses: Agents. The
/// observers are resolved on the first change rather than injected, because Agents' observer naturally uses
/// <see cref="ICatalog"/>, and injecting it into <see cref="CatalogSync"/> could make that a cycle that stops
/// startup.
/// </summary>
internal sealed class ModelStatusObservers(IServiceProvider services, ILogger<ModelStatusObservers> logger)
{
    private readonly Lazy<IReadOnlyList<IModelStatusObserver>> _observers = new(() => [.. services.GetServices<IModelStatusObserver>()]);

    /// <summary>
    /// Called once the changes have committed, so an observer never hears of one that rolled back. Observers
    /// must return at once and never throw, but one that does can't undo a committed change, or stop the others.
    /// </summary>
    public void Notify(string provider, IReadOnlyList<ModelStatusChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        foreach (var observer in _observers.Value)
        {
            try
            {
                observer.OnChanged(changes);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception, "Telling {Observer} about {Changes} {Provider} models failed", observer.GetType().Name, changes.Count, provider);
            }
        }
    }
}
