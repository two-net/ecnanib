using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// What the Catalog tells whatever depends on a model, when a sync marks the model unavailable:
/// an agent that a model of its team is gone for moves to Attention required, and no other model
/// is put in its place (FR-CAT-8). The Catalog may not use Agents, so it declares this
/// interface, and Agents implements and registers it. See "When a lower component must trigger a
/// higher one" in docs/architecture.md.
/// </summary>
/// <remarks>
/// <para>
/// Agents implements it: a role of an agent's team is given a model of the catalog, and an
/// agent that runs with a model that goes moves to Attention required. Its roles keep the
/// models that its user gave them.
/// </para>
/// <para>
/// It is told inside the transaction that stores the sync, before the rows of the models
/// change, with the context that holds the transaction. So what an implementation changes is
/// stored when the models are marked, and not when they are not. It is told once, when models
/// that were active become unavailable, and not again while they stay so, nor when one is active
/// again.
/// </para>
/// <para>
/// An implementation commits nothing, and takes the owner of what it changes from its own rows:
/// the sync may run in the request of a user whose key listed the models, and the agents are
/// those of every user. The transaction holds the row of the provider and the rows of its
/// models, and two providers are synced at the same time, so an implementation locks its rows in
/// one order, and takes no lock on a model while it holds one on an account. It is a singleton,
/// and takes nothing of Credentials, which itself takes the Catalog.
/// </para>
/// <para>
/// An implementation that fails fails the sync: it is recorded as one that failed, and the
/// catalog of the provider is as it was, for every user, until a later sync can tell it. A model
/// that an agent selected while the sync was storing is not among what the implementation
/// finds. The call that then reports that the model does not exist is what moves that agent.
/// </para>
/// </remarks>
public interface IModelDependents
{
    /// <summary>Models of a provider are being marked unavailable.</summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="provider">The provider whose models they are.</param>
    /// <param name="modelIds">The ids of the models in the catalog. There is at least one.</param>
    /// <param name="cancellationToken">Cancels the sync.</param>
    Task ModelsUnavailableAsync(
        EcnanibDbContext context, AiProviderCode provider, IReadOnlyList<Guid> modelIds, CancellationToken cancellationToken);
}
