using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class CouncilAssignmentConfiguration : IEntityTypeConfiguration<CouncilAssignment>
{
    public void Configure(EntityTypeBuilder<CouncilAssignment> builder)
    {
        builder.HasKey(c => new { c.AgentId, c.Role });
        builder.Property(c => c.Model).HasMaxLength(200);
        builder.HasOne(c => c.Agent).WithMany(a => a.CouncilAssignments).HasForeignKey(c => c.AgentId).OnDelete(DeleteBehavior.Cascade);
        // FR-AI-05: the Risk Manager role cannot be disabled.
        builder.ToTable(t => t.HasCheckConstraint("ck_council_assignments_risk_manager_enabled", "NOT (role = 'RiskManager' AND enabled = false)"));
    }
}
