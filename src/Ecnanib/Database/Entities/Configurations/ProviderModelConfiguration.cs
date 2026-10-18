using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class ProviderModelConfiguration : IEntityTypeConfiguration<ProviderModel>
{
    public void Configure(EntityTypeBuilder<ProviderModel> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.ModelId).HasMaxLength(200);
        builder.Property(m => m.DisplayName).HasMaxLength(200);
        builder.Property(m => m.Capabilities).HasColumnType("jsonb");
        builder.HasIndex(m => new { m.Provider, m.ModelId }).IsUnique();
    }
}
