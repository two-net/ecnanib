using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.BackgroundWork;

/// <summary>
/// What the database keeps about a job, as a row of the <c>background_jobs</c> table: its latest
/// run. It is what lets a schedule go on after a restart (NFR-ARC-4). A job that has never run
/// has no row.
/// </summary>
/// <remarks>
/// This is state, not a log: one row per job, overwritten by each run. Why a run failed is in
/// the log and never here (NFR-DAT-7).
/// </remarks>
internal sealed class BackgroundJobState(
    string name,
    DateTimeOffset lastStartedAt,
    DateTimeOffset? lastFinishedAt,
    BackgroundJobOutcome? lastOutcome)
{
    /// <summary>The job, by <see cref="IBackgroundJob.Name"/>.</summary>
    public string Name { get; } = name;

    /// <summary>When the latest run started. It is written before the run begins.</summary>
    public DateTimeOffset LastStartedAt { get; } = lastStartedAt;

    /// <summary>
    /// When the latest run ended. Null while it is running, and for good when the process ended
    /// before the run did.
    /// </summary>
    public DateTimeOffset? LastFinishedAt { get; } = lastFinishedAt;

    /// <summary>How the latest run ended. Null together with <see cref="LastFinishedAt"/>.</summary>
    public BackgroundJobOutcome? LastOutcome { get; } = lastOutcome;
}

/// <summary>How a run ended.</summary>
internal enum BackgroundJobOutcome
{
    /// <summary>The job did its work.</summary>
    Succeeded,

    /// <summary>The job threw. The error is in the log.</summary>
    Failed,

    /// <summary>The application stopped the run before the job was done, so the work is still to do.</summary>
    Stopped,
}

internal sealed class BackgroundJobStateConfiguration : IEntityTypeConfiguration<BackgroundJobState>
{
    public void Configure(EntityTypeBuilder<BackgroundJobState> builder)
    {
        builder.ToTable("background_jobs");
        builder.HasKey(job => job.Name);
        builder.Property(job => job.LastStartedAt);
        builder.Property(job => job.LastFinishedAt);
        builder.Property(job => job.LastOutcome);
    }
}
