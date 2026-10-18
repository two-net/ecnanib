using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class ExchangeKeyConfiguration : IEntityTypeConfiguration<ExchangeKey>
{
    public void Configure(EntityTypeBuilder<ExchangeKey> builder)
    {
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Label).HasMaxLength(100);
        builder.Property(k => k.PermissionsSnapshot).HasColumnType("jsonb");
        builder.HasOne(k => k.User).WithMany(u => u.ExchangeKeys).HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(k => new { k.UserId, k.Label }).IsUnique();
    }
}
