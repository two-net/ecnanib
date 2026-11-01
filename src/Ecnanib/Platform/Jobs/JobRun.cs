using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Jobs;

/// <summary>One run of a job (SRS 6.1). The newest row per job is also what its schedule reads.</summary>
internal sealed class JobRun
{
    public long Id { get; init; }

    public required string JobName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Null while running, and after a crash, when nobody knows how long the run lasted.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    public required JobStatus Status { get; init; }

    /// <summary>
    /// A scrubbed summary, or an exception's type name. Never exception text: this table has no
    /// SanitizingSink, and it's for administrators, who mustn't see prompts a provider's error may
    /// echo (FR-ADM-01). The text is in the log, under the same correlation ID.
    /// </summary>
    public string? Details { get; init; }

    public string? CorrelationId { get; init; }
}

internal sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        builder.ToTable("job_runs");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Status).HasConversion<string>();
        builder.HasIndex(run => new { run.JobName, run.StartedAt });
    }
}
