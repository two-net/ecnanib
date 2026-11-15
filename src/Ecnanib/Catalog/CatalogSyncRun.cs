using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Catalog;

/// <summary>
/// Why a provider's catalog was synced (FR-CAT-2, FR-CAT-3). A member is stored by its name, so
/// renaming one changes stored data and needs a migration.
/// </summary>
public enum CatalogSyncCause
{
    /// <summary>
    /// The job ran: on its schedule, or as the application started with a catalog that was
    /// empty. The service's own key for the provider was used.
    /// </summary>
    Scheduled,

    /// <summary>A sync was asked for, outside the schedule. The service's own key was used.</summary>
    OnDemand,

    /// <summary>
    /// The provider took a user's key, and the configuration file holds no key of the service
    /// for it. The user's key was used, and is not kept.
    /// </summary>
    KeyValidated,
}

/// <summary>
/// How a sync ended. A member is stored by its name, so renaming one changes stored data and
/// needs a migration.
/// </summary>
public enum CatalogSyncResult
{
    /// <summary>The provider listed its models, and the catalog holds what the list said.</summary>
    Succeeded,

    /// <summary>The catalog of the provider is as it was before (FR-CAT-7).</summary>
    Failed,
}

/// <summary>
/// Why a sync failed, in a word of the application's own. A member is stored by its name, so
/// renaming one changes stored data and needs a migration.
/// </summary>
public enum CatalogSyncFailure
{
    /// <summary>The provider answered as it answers a wrong key.</summary>
    KeyRefused,

    /// <summary>The provider took longer than the time a list may take.</summary>
    TimedOut,

    /// <summary>
    /// The provider gave no list of its models: no connection, a limit on requests, a fault of
    /// the provider, or an answer that is not its list, an empty one included.
    /// </summary>
    NoList,

    /// <summary>The list came, and storing it went wrong. The log says how.</summary>
    Error,
}

/// <summary>
/// One sync of one provider's catalog, as a row of the <c>catalog_sync_runs</c> table: when it
/// ran, how it ended, and how many models it added, changed, and marked unavailable (FR-CAT-7).
/// See docs/catalog.md.
/// </summary>
/// <remarks>
/// <para>
/// A row says that a sync failed and, in one word of the application's own, why. It holds no
/// text of an error and nothing that a provider wrote: those are in the log (NFR-DAT-7).
/// </para>
/// <para>
/// A row names no user and no key. A sync that a user's key was used for is told apart by its
/// cause alone.
/// </para>
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="provider">The provider whose catalog was synced.</param>
/// <param name="cause">Why it was synced.</param>
/// <param name="startedAt">When the sync started, before the provider was asked.</param>
/// <param name="finishedAt">When the sync ended.</param>
/// <param name="result">How it ended.</param>
/// <param name="failure">Why it failed, or null when it did not.</param>
/// <param name="added">How many models the sync found that the catalog did not hold.</param>
/// <param name="changed">
/// How many models of the catalog the sync changed: one that the provider now says something
/// else about, and one that was unavailable and is listed again.
/// </param>
/// <param name="unavailable">How many models the sync marked unavailable.</param>
public sealed class CatalogSyncRun(
    Guid id,
    AiProviderCode provider,
    CatalogSyncCause cause,
    DateTimeOffset startedAt,
    DateTimeOffset finishedAt,
    CatalogSyncResult result,
    CatalogSyncFailure? failure,
    int added,
    int changed,
    int unavailable)
{
    public Guid Id { get; } = id;

    public AiProviderCode Provider { get; } = provider;

    public CatalogSyncCause Cause { get; } = cause;

    public DateTimeOffset StartedAt { get; } = startedAt;

    public DateTimeOffset FinishedAt { get; } = finishedAt;

    public CatalogSyncResult Result { get; } = result;

    public CatalogSyncFailure? Failure { get; } = failure;

    public int Added { get; } = added;

    public int Changed { get; } = changed;

    public int Unavailable { get; } = unavailable;
}

internal sealed class CatalogSyncRunConfiguration : IEntityTypeConfiguration<CatalogSyncRun>
{
    public void Configure(EntityTypeBuilder<CatalogSyncRun> builder)
    {
        builder.ToTable("catalog_sync_runs");
        builder.HasKey(run => run.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(run => run.Id).ValueGeneratedNever();
        builder.Property(run => run.Provider);
        builder.Property(run => run.Cause);
        builder.Property(run => run.StartedAt);
        builder.Property(run => run.FinishedAt);
        builder.Property(run => run.Result);
        builder.Property(run => run.Failure);
        builder.Property(run => run.Added);
        builder.Property(run => run.Changed);
        builder.Property(run => run.Unavailable);

        // The latest sync of a provider, and its syncs in order.
        builder.HasIndex(run => new { run.Provider, run.StartedAt });

        // A provider is a seeded row, which is never deleted while a sync points at it.
        builder.HasOne<AiProvider>()
            .WithMany()
            .HasForeignKey(run => run.Provider)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
