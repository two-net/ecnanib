using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class DecisionRoleOutputConfiguration : IEntityTypeConfiguration<DecisionRoleOutput>
{
    public void Configure(EntityTypeBuilder<DecisionRoleOutput> builder)
    {
        builder.HasKey(r => new { r.DecisionId, r.Role });
        builder.Property(r => r.Model).HasMaxLength(200);
        builder.HasOne(r => r.Decision).WithMany(d => d.RoleOutputs).HasForeignKey(r => r.DecisionId).OnDelete(DeleteBehavior.Cascade);
    }
}
