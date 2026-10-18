using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).HasMaxLength(100);
        builder.Property(a => a.Symbol).HasMaxLength(30);
        builder.Property(a => a.StrategyId).HasMaxLength(100);
        builder.Property(a => a.Parameters).HasColumnType("jsonb");
        builder.Property(a => a.Version).IsRowVersion();
        builder.OwnsOne(a => a.RiskLimits, risk => risk.ToJson());
        builder.Navigation(a => a.RiskLimits).IsRequired();
        builder.HasOne(a => a.User).WithMany(u => u.Agents).HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(a => a.ExchangeKey).WithMany().HasForeignKey(a => a.ExchangeKeyId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.State);
    }
}
