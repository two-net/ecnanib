using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).HasMaxLength(254);
        builder.Property(u => u.NormalizedEmail).HasMaxLength(254);
        builder.Property(u => u.SecurityStamp).HasMaxLength(64);
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();
    }
}
