using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class DecisionConfiguration : IEntityTypeConfiguration<Decision>
{
    public void Configure(EntityTypeBuilder<Decision> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Rationale).HasMaxLength(4000);
        builder.Property(d => d.Targets).HasColumnType("jsonb");
        builder.Property(d => d.LimitsCheck).HasColumnType("jsonb");
        builder.HasOne(d => d.Evaluation).WithOne(e => e.Decision).HasForeignKey<Decision>(d => d.EvaluationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(d => d.Trade).WithMany().HasForeignKey(d => d.TradeId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(d => d.TradeId);
    }
}
