using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class EvaluationConfiguration : IEntityTypeConfiguration<Evaluation>
{
    public void Configure(EntityTypeBuilder<Evaluation> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Symbol).HasMaxLength(30);
        builder.Property(e => e.StrategyOutput).HasColumnType("jsonb");
        builder.HasOne(e => e.Agent).WithMany().HasForeignKey(e => e.AgentId).OnDelete(DeleteBehavior.Cascade);
        // FR-AGT-09: exactly one evaluation per agent and candle; also the decision-history index.
        builder.HasIndex(e => new { e.AgentId, e.CandleOpenTime }).IsUnique();
    }
}
