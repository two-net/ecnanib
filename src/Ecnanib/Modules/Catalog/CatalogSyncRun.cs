using System.Text.Json;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// One provider's part of a sync (SRS 6.1 CatalogSyncRun, FR-CAT-08). It's inserted as the run starts and
/// closed in the transaction that writes its results, so its counts always match what changed. A row with
/// no <see cref="FinishedAt"/> was interrupted, and the run's <c>job_runs</c> row says how.
/// </summary>
internal sealed class CatalogSyncRun
{
    /// <summary>The longest <see cref="CatalogSyncTrigger"/> name, with room to spare.</summary>
    public const int TriggerMaxLength = 16;

    public long Id { get; init; }

    public required string Provider { get; init; }

    public required CatalogSyncTrigger Trigger { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The valid keys it asked.</summary>
    public required int Keys { get; init; }

    /// <summary>The keys the provider didn't answer for.</summary>
    public int KeysFailed { get; set; }

    public int ModelsAdded { get; set; }

    /// <summary>Models whose provider-reported details changed.</summary>
    public int ModelsUpdated { get; set; }

    /// <summary>0 until #25 deprecates models (FR-CAT-04).</summary>
    public int ModelsDeprecated { get; set; }

    /// <summary>0 until #25 retires models (FR-CAT-04).</summary>
    public int ModelsRetired { get; set; }

    /// <summary>
    /// <see cref="SyncError"/>s as JSON, or null when there were none. Never exception or provider text, which
    /// can quote a request, for the reason <c>job_runs.details</c> leaves it out: administrators will read this.
    /// </summary>
    public string? Errors { get; set; }

    /// <summary>The job run's, so its log events can be found.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Why one key's list failed: the normalized category (EXT-AI-04) and the HTTP status, if there was one.</summary>
internal sealed record SyncError(Guid CredentialId, AiFailure Failure, int? StatusCode)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string? ToJson(IReadOnlyCollection<SyncError> errors) => errors.Count == 0 ? null : JsonSerializer.Serialize(errors, Json);
}

internal sealed class CatalogSyncRunConfiguration : IEntityTypeConfiguration<CatalogSyncRun>
{
    public void Configure(EntityTypeBuilder<CatalogSyncRun> builder)
    {
        builder.ToTable("catalog_sync_runs");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Trigger).HasConversion<string>().HasMaxLength(CatalogSyncRun.TriggerMaxLength);
        builder.Property(run => run.Errors).HasColumnType("jsonb");
        builder.HasIndex(run => new { run.Provider, run.StartedAt });
    }
}
