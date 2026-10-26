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
        // NFR-SEC-03: an agent may reference only a key of its own user, and the database enforces it through the key's
        // (id, user_id) pair. NoAction rather than SetNull: PostgreSQL would null every referencing column, user_id
        // included; and NoAction rather than Restrict so that deleting a user cascades to keys and agents in one statement.
        // The keys module clears exchange_key_id itself before it deletes a key (FR-KEY-06).
        builder.HasOne(a => a.ExchangeKey).WithMany().HasForeignKey(a => new { a.ExchangeKeyId, a.UserId }).HasPrincipalKey(k => new { k.Id, k.UserId }).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.State);
    }
}
