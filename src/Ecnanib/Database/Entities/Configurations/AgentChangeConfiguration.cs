using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class AgentChangeConfiguration : IEntityTypeConfiguration<AgentChange>
{
    public void Configure(EntityTypeBuilder<AgentChange> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Field).HasMaxLength(100);
        builder.Property(c => c.OldValue).HasColumnType("jsonb");
        builder.Property(c => c.NewValue).HasColumnType("jsonb");
        builder.HasOne(c => c.Agent).WithMany().HasForeignKey(c => c.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.AgentId, c.Timestamp });
    }
}
