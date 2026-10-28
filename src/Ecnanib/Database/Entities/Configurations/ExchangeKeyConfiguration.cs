using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class ExchangeKeyConfiguration : IEntityTypeConfiguration<ExchangeKey>
{
    public void Configure(EntityTypeBuilder<ExchangeKey> builder)
    {
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Label).HasMaxLength(100);
        // The columns keep the names of the plain text they held before the ProtectStoredKeys migration converted them.
        builder.Property(k => k.ProtectedApiKey).HasColumnName("api_key");
        builder.Property(k => k.ProtectedApiSecret).HasColumnName("api_secret");
        builder.Property(k => k.Cipher).HasDefaultValue(StoredKeyCipher.Plain);
        builder.Property(k => k.PermissionsSnapshot).HasColumnType("jsonb");
        builder.HasOne(k => k.User).WithMany(u => u.ExchangeKeys).HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(k => new { k.UserId, k.Label }).IsUnique();
    }
}
