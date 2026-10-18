using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class ProviderSyncStateConfiguration : IEntityTypeConfiguration<ProviderSyncState>
{
    public void Configure(EntityTypeBuilder<ProviderSyncState> builder)
    {
        builder.HasKey(s => s.Provider);
    }
}
