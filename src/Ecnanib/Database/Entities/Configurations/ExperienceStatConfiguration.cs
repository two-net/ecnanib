using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class ExperienceStatConfiguration : IEntityTypeConfiguration<ExperienceStat>
{
    public void Configure(EntityTypeBuilder<ExperienceStat> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Model).HasMaxLength(200);
        builder.OwnsOne(s => s.Metrics, metrics => metrics.ToJson());
        builder.Navigation(s => s.Metrics).IsRequired();
        builder.HasOne(s => s.Agent).WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        // FR-EXP-05: system rows carry no owner, user rows a user, agent rows both.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_experience_stats_scope",
            "(scope = 'System' AND agent_id IS NULL AND user_id IS NULL) OR " +
            "(scope = 'User' AND agent_id IS NULL AND user_id IS NOT NULL) OR " +
            "(scope = 'Agent' AND agent_id IS NOT NULL AND user_id IS NOT NULL)"));
        // One row per scope key; NULL agent/user take part in the comparison (PostgreSQL 15 NULLS NOT DISTINCT).
        builder.HasIndex(s => new { s.Scope, s.AgentId, s.UserId, s.Provider, s.Model, s.Role, s.Timeframe, s.MarketType })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}
