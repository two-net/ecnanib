using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class CandleConfiguration : IEntityTypeConfiguration<Candle>
{
    public void Configure(EntityTypeBuilder<Candle> builder)
    {
        builder.HasKey(c => new { c.Environment, c.MarketType, c.Symbol, c.Interval, c.OpenTime });
        builder.Property(c => c.Symbol).HasMaxLength(30);
        // The retention sweep deletes by open time across every market.
        builder.HasIndex(c => c.OpenTime);
    }
}
