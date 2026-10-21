using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.ClientOrderId).HasMaxLength(36);
        builder.Property(o => o.ListClientOrderId).HasMaxLength(36);
        builder.Property(o => o.Version).IsRowVersion();
        builder.HasOne(o => o.Agent).WithMany().HasForeignKey(o => o.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(o => o.Decision).WithMany().HasForeignKey(o => o.DecisionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(o => o.Trade).WithMany().HasForeignKey(o => o.TradeId).OnDelete(DeleteBehavior.SetNull);
        // FR-EXE-07, NFR-REL-02: deterministic client ids never duplicate an order.
        builder.HasIndex(o => o.ClientOrderId).IsUnique();
        builder.HasIndex(o => new { o.AgentId, o.CreatedAt });
        builder.HasIndex(o => o.ExchangeOrderId);
        builder.HasIndex(o => o.ExchangeAlgoId);
        builder.HasIndex(o => o.TradeId);
    }
}
