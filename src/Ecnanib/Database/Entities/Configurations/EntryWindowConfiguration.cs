using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class EntryWindowConfiguration : IEntityTypeConfiguration<EntryWindow>
{
    public void Configure(EntityTypeBuilder<EntryWindow> builder)
    {
        builder.HasKey(w => w.Id);
        // Ordinal: day ranges compare numerically, not as text.
        builder.Property(w => w.StartDay).HasConversion<int>();
        builder.Property(w => w.EndDay).HasConversion<int>();
        builder.HasOne(w => w.Agent).WithMany(a => a.EntryWindows).HasForeignKey(w => w.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_entry_windows_kind",
            "(kind = 'OneOff' AND start_at IS NOT NULL AND end_at IS NOT NULL AND start_day IS NULL AND start_time IS NULL AND end_day IS NULL AND end_time IS NULL) OR " +
            "(kind = 'Recurring' AND start_day IS NOT NULL AND start_time IS NOT NULL AND end_day IS NOT NULL AND end_time IS NOT NULL AND start_at IS NULL AND end_at IS NULL)"));
    }
}
