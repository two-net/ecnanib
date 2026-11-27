using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Availability;

/// <summary>
/// A time that the service was up for without a break: from when an instance took the
/// background work up, to when it last said that it runs (NFR-REL-1). The time between two rows
/// is time that nothing said so for, and counts as down. See <see cref="Uptime"/> and
/// "Availability" in docs/reliability.md.
/// </summary>
/// <remarks>
/// A row is of the service, and of no user. It is written by the instance that runs the
/// background work, which lengthens its own row with each beat, so the table grows by a row with
/// each start and each break, and not with each minute.
/// </remarks>
/// <param name="id">The id of the row, which the application assigns.</param>
/// <param name="beganAt">From when the service was up.</param>
/// <param name="beatAt">When it last said that it was.</param>
internal sealed class UpSpan(Guid id, DateTimeOffset beganAt, DateTimeOffset beatAt)
{
    public Guid Id { get; } = id;

    public DateTimeOffset BeganAt { get; } = beganAt;

    public DateTimeOffset BeatAt { get; } = beatAt;
}

internal sealed class UpSpanConfiguration : IEntityTypeConfiguration<UpSpan>
{
    public void Configure(EntityTypeBuilder<UpSpan> builder)
    {
        builder.ToTable("up_spans");
        builder.HasKey(span => span.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(span => span.Id).ValueGeneratedNever();
        builder.Property(span => span.BeganAt);
        builder.Property(span => span.BeatAt);

        // The first of them, and those of a month.
        builder.HasIndex(span => span.BeganAt);
    }
}
