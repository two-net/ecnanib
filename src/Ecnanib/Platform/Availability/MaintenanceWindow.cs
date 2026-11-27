using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Availability;

/// <summary>
/// Maintenance that an operator announced: from when to when the service may not answer
/// (NFR-REL-6). A banner on every page says so until it is over, and what the service is down
/// for during it is left out of its availability when it was announced in advance (NFR-REL-1).
/// See <see cref="MaintenanceWindows"/> and "Announced maintenance" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// A row is of the service, and of no user: who announced it is in the audit trail, with the id
/// of the row as its target, and not here.
/// </para>
/// <para>
/// <b>Whether it was announced in advance is written when it is announced.</b> It is held
/// against the notice that the configuration file asks for then, so a file that asks for
/// another notice later changes nothing of a month that was measured.
/// </para>
/// </remarks>
/// <param name="id">The id of the row, which the application assigns.</param>
/// <param name="startsAt">From when.</param>
/// <param name="endsAt">Until when, which is later than <paramref name="startsAt"/> unless it was ended as it began.</param>
/// <param name="announcedAt">When it was announced.</param>
/// <param name="inAdvance">Whether that was as long before its start as the configuration file asked for.</param>
internal sealed class MaintenanceWindow(Guid id, DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset announcedAt, bool inAdvance)
{
    public Guid Id { get; } = id;

    public DateTimeOffset StartsAt { get; } = startsAt;

    public DateTimeOffset EndsAt { get; } = endsAt;

    public DateTimeOffset AnnouncedAt { get; } = announcedAt;

    public bool InAdvance { get; } = inAdvance;
}

internal sealed class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        builder.ToTable("maintenance_windows");
        builder.HasKey(window => window.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(window => window.Id).ValueGeneratedNever();
        builder.Property(window => window.StartsAt);
        builder.Property(window => window.EndsAt);
        builder.Property(window => window.AnnouncedAt);
        builder.Property(window => window.InAdvance);

        // What is not over, which every page asks for, and what a month holds.
        builder.HasIndex(window => window.EndsAt);
    }
}
