using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class LogEntryConfiguration : IEntityTypeConfiguration<LogEntry>
{
    public void Configure(EntityTypeBuilder<LogEntry> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Component).HasMaxLength(200);
        // Ordinal: "Warning and above" must compare numerically, not as text (FR-LOG-03, FR-LOG-07).
        builder.Property(l => l.Level).HasConversion<int>();
        builder.Property(l => l.Context).HasColumnType("jsonb");
        // No foreign keys by design (FR-LOG-08); see the entity summary.
        builder.HasIndex(l => l.Timestamp);
        builder.HasIndex(l => new { l.UserId, l.Timestamp });
        builder.HasIndex(l => new { l.AgentId, l.Timestamp });
    }
}
