using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class ProviderKeyConfiguration : IEntityTypeConfiguration<ProviderKey>
{
    public void Configure(EntityTypeBuilder<ProviderKey> builder)
    {
        builder.HasKey(k => k.Id);
        // The column keeps the name of the plain text it held before the ProtectStoredKeys migration converted it.
        builder.Property(k => k.ProtectedApiKey).HasColumnName("api_key");
        builder.Property(k => k.Cipher).HasDefaultValue(StoredKeyCipher.Plain);
        builder.HasOne(k => k.User).WithMany(u => u.ProviderKeys).HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(k => new { k.UserId, k.Provider }).IsUnique();
    }
}
