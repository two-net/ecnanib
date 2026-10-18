using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class DisclaimerAcceptanceConfiguration : IEntityTypeConfiguration<DisclaimerAcceptance>
{
    public void Configure(EntityTypeBuilder<DisclaimerAcceptance> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Version).HasMaxLength(50);
        builder.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.UserId, a.Version }).IsUnique();
    }
}
