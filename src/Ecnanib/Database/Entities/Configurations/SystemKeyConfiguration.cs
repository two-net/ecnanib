using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class SystemKeyConfiguration : IEntityTypeConfiguration<SystemKey>
{
    public void Configure(EntityTypeBuilder<SystemKey> builder)
    {
        builder.HasKey(k => k.Purpose);
        builder.Property(k => k.Purpose).HasMaxLength(100);
    }
}
