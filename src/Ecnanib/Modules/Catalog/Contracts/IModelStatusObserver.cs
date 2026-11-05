namespace Ecnanib.Modules.Catalog.Contracts;

/// <summary>
/// Told after a sync commits a change to models' statuses (FR-CAT-04): Agents warns the agents that use a
/// deprecated model, and moves them off a retired one. Catalog can't depend on Agents, so Agents implements
/// this. It's called on the sync's job, so it must return at once, and it's in process, so a crash can lose
/// it: the observer needs a schedule that recovers what it missed. See Inversions in docs/architecture.md.
/// </summary>
public interface IModelStatusObserver
{
    /// <param name="changes">One provider's changes from one sync, never empty.</param>
    void OnChanged(IReadOnlyList<ModelStatusChange> changes);
}

/// <summary>A model whose status a sync changed.</summary>
/// <param name="Status">Deprecated, Retired, or Active when a key lists it again.</param>
public sealed record ModelStatusChange(string Provider, string ModelId, ModelStatus Status, ModelStatus PreviousStatus);
