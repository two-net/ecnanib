using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class ShadowOutcomeConfiguration : IEntityTypeConfiguration<ShadowOutcome>
{
    public void Configure(EntityTypeBuilder<ShadowOutcome> builder)
    {
        builder.HasKey(s => s.DecisionId);
        builder.Property(s => s.Detail).HasColumnType("jsonb");
        builder.HasOne(s => s.Decision).WithOne(d => d.ShadowOutcome).HasForeignKey<ShadowOutcome>(s => s.DecisionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => s.Result).HasDatabaseName("ix_shadow_outcomes_pending").HasFilter("result = 'Pending'");
    }
}
