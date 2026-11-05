using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.MarketData;

/// <summary>
/// A symbol Binance lists, with its filters: SRS 6.1's ExchangeSymbol. The rows for a market and
/// environment are Binance's latest list, so a symbol it drops is deleted. Prices are in the quote asset,
/// and quantities in the base asset.
/// </summary>
internal sealed class ExchangeSymbol
{
    /// <summary>Binance's symbols are at most 20 characters.</summary>
    public const int SymbolMaxLength = 20;

    public const int TextMaxLength = 32;

    public required Market Market { get; init; }

    public required TradingEnvironment Environment { get; init; }

    public required string Symbol { get; init; }

    public required string Status { get; set; }

    public required string BaseAsset { get; set; }

    public required string QuoteAsset { get; set; }

    public string? ContractType { get; set; }

    public required string[] OrderTypes { get; set; }

    public decimal? MinPrice { get; set; }

    public decimal? MaxPrice { get; set; }

    public decimal? TickSize { get; set; }

    public decimal? MinQuantity { get; set; }

    public decimal? MaxQuantity { get; set; }

    public decimal? StepSize { get; set; }

    public decimal? MarketMinQuantity { get; set; }

    public decimal? MarketMaxQuantity { get; set; }

    public decimal? MarketStepSize { get; set; }

    public decimal? MinNotional { get; set; }

    public decimal? MaxNotional { get; set; }

    /// <summary>PERCENT_PRICE's multipliers, as <see cref="PercentPriceRules"/> has them.</summary>
    public decimal? PercentBuyUp { get; set; }

    public decimal? PercentBuyDown { get; set; }

    public decimal? PercentSellUp { get; set; }

    public decimal? PercentSellDown { get; set; }

    public int? PercentAveragePriceMinutes { get; set; }

    /// <summary>When Binance gave the list this row came from, by this server's clock.</summary>
    public required DateTimeOffset RefreshedAt { get; set; }

    public static ExchangeSymbol From(Market market, TradingEnvironment environment, SymbolRules rules, DateTimeOffset refreshedAt)
    {
        var row = new ExchangeSymbol
        {
            Market = market,
            Environment = environment,
            Symbol = rules.Symbol,
            Status = rules.Status,
            BaseAsset = rules.BaseAsset,
            QuoteAsset = rules.QuoteAsset,
            OrderTypes = [],
            RefreshedAt = refreshedAt,
        };
        row.Update(rules, refreshedAt);

        return row;
    }

    /// <summary>Takes Binance's latest copy.</summary>
    public void Update(SymbolRules rules, DateTimeOffset refreshedAt)
    {
        Status = rules.Status;
        BaseAsset = rules.BaseAsset;
        QuoteAsset = rules.QuoteAsset;
        ContractType = rules.ContractType;
        OrderTypes = [.. rules.OrderTypes];
        (MinPrice, MaxPrice, TickSize) = (rules.Price?.Minimum, rules.Price?.Maximum, rules.Price?.TickSize);
        (MinQuantity, MaxQuantity, StepSize) = (rules.Quantity?.Minimum, rules.Quantity?.Maximum, rules.Quantity?.StepSize);
        (MarketMinQuantity, MarketMaxQuantity, MarketStepSize) = (rules.MarketQuantity?.Minimum, rules.MarketQuantity?.Maximum, rules.MarketQuantity?.StepSize);
        (MinNotional, MaxNotional) = (rules.Notional?.Minimum, rules.Notional?.Maximum);
        (PercentBuyUp, PercentBuyDown, PercentSellUp, PercentSellDown, PercentAveragePriceMinutes) = (
            rules.PercentPrice?.BuyUp,
            rules.PercentPrice?.BuyDown,
            rules.PercentPrice?.SellUp,
            rules.PercentPrice?.SellDown,
            rules.PercentPrice?.AveragePriceMinutes);
        RefreshedAt = refreshedAt;
    }

    public ExchangeSymbolInfo ToInfo() => new(
        Market,
        Environment,
        Symbol,
        Status,
        BaseAsset,
        QuoteAsset,
        ContractType,
        OrderTypes,
        MinPrice is { } minPrice && MaxPrice is { } maxPrice && TickSize is { } tick ? new PriceFilter(minPrice, maxPrice, tick) : null,
        MinQuantity is { } minQuantity && MaxQuantity is { } maxQuantity && StepSize is { } step ? new QuantityFilter(minQuantity, maxQuantity, step) : null,
        MarketMinQuantity is { } marketMin && MarketMaxQuantity is { } marketMax && MarketStepSize is { } marketStep
            ? new QuantityFilter(marketMin, marketMax, marketStep)
            : null,
        MinNotional is { } minNotional ? new NotionalFilter(minNotional, MaxNotional) : null,
        RefreshedAt,
        PercentBuyUp is { } buyUp && PercentBuyDown is { } buyDown && PercentSellUp is { } sellUp && PercentSellDown is { } sellDown
            ? new PercentPriceFilter(buyUp, buyDown, sellUp, sellDown, PercentAveragePriceMinutes)
            : null);

    public static ExchangeSymbolInfo ToInfo(Market market, TradingEnvironment environment, SymbolRules rules, DateTimeOffset refreshedAt) =>
        From(market, environment, rules, refreshedAt).ToInfo();
}

internal sealed class ExchangeSymbolConfiguration : IEntityTypeConfiguration<ExchangeSymbol>
{
    public void Configure(EntityTypeBuilder<ExchangeSymbol> builder)
    {
        builder.ToTable("exchange_symbols");
        builder.HasKey(symbol => new { symbol.Market, symbol.Environment, symbol.Symbol });
        builder.Property(symbol => symbol.Market).HasConversion<string>().HasMaxLength(CachedCandle.NameMaxLength);
        builder.Property(symbol => symbol.Environment).HasConversion<string>().HasMaxLength(CachedCandle.NameMaxLength);
        builder.Property(symbol => symbol.Symbol).HasMaxLength(ExchangeSymbol.SymbolMaxLength);
        builder.Property(symbol => symbol.Status).HasMaxLength(ExchangeSymbol.TextMaxLength);
        builder.Property(symbol => symbol.BaseAsset).HasMaxLength(ExchangeSymbol.TextMaxLength);
        builder.Property(symbol => symbol.QuoteAsset).HasMaxLength(ExchangeSymbol.TextMaxLength);
        builder.Property(symbol => symbol.ContractType).HasMaxLength(ExchangeSymbol.TextMaxLength);
        builder.Property(symbol => symbol.OrderTypes).HasColumnType("text[]");
    }
}
