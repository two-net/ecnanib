using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class TradeConfiguration : IEntityTypeConfiguration<Trade>
{
    public void Configure(EntityTypeBuilder<Trade> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Symbol).HasMaxLength(30);
        builder.Property(t => t.StopHistory).HasColumnType("jsonb");
        builder.Property(t => t.Targets).HasColumnType("jsonb");
        builder.Property(t => t.Version).IsRowVersion();
        builder.HasOne(t => t.Agent).WithMany().HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(t => t.EntryDecision).WithMany().HasForeignKey(t => t.EntryDecisionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.AgentId, t.OpenedAt });
        builder.HasIndex(t => t.EntryDecisionId);
        builder.HasIndex(t => t.AgentId).HasDatabaseName("ix_trades_agent_id_open").HasFilter("closed_at IS NULL");
    }
}
