using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ExchangeSide = Ecnanib.Modules.ExchangeAdapter.Contracts.OrderSide;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// One trade in the journal, SRS 6.1's Trade (FR-EXE-07): one entry order and what became of it. An add is a trade of
/// its own, because the Risk Engine sized and risked it on its own. Its exits are its entry's parts in
/// <c>protections</c>. It opens when its entry's first part is protected, and closes once the entry has ended and
/// every part has. Its figures are provisional, from the order rows, until <see cref="SettledAt"/>, when they come from
/// Binance's fills and funding. Prices are in <see cref="QuoteAsset"/>, quantities in <see cref="BaseAsset"/>, and
/// amounts of money in <see cref="QuoteAsset"/> (DATA-R-02). See The trade journal in docs/architecture.md.
/// </summary>
internal sealed class TradeRow
{
    public const int AssetMaxLength = 20;

    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required Guid AgentId { get; init; }

    /// <summary>The entry order the trade is.</summary>
    public required Guid EntryId { get; init; }

    /// <summary>The entry's exchange key, which settling opens: the agent's may have changed once it had no orders.</summary>
    public required Guid CredentialId { get; init; }

    public required Market Market { get; init; }

    public required TradingEnvironment Environment { get; init; }

    public required string Symbol { get; init; }

    public required string BaseAsset { get; init; }

    public required string QuoteAsset { get; init; }

    public required PositionSide Side { get; init; }

    public TradeStatus Status { get; set; }

    /// <summary>When its first part filled.</summary>
    public required DateTimeOffset OpenedAt { get; set; }

    /// <summary>When its last part ended, from the parts, never when it was booked.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>What its parts held: on Spot, net of the commission taken in the base asset, and rounded to the lot.</summary>
    public decimal Quantity { get; set; }

    /// <summary>The entry's average fill price.</summary>
    public decimal EntryPrice { get; set; }

    /// <summary>The entry's protective stop, which its R-multiple is measured against.</summary>
    public required decimal InitialStop { get; init; }

    /// <summary>What was sold or bought back with a known price.</summary>
    public decimal ExitQuantity { get; set; }

    /// <summary>The average price of <see cref="ExitQuantity"/>. Null when nothing was priced.</summary>
    public decimal? ExitPrice { get; set; }

    /// <summary>What ended without a price: dust, abandoned, left to the user, or closed outside Ecnanib with no trade found.</summary>
    public decimal UnpricedQuantity { get; set; }

    /// <summary>Every fill's fee, in the quote asset. Null when the fills couldn't be read.</summary>
    public decimal? Fees { get; set; }

    /// <summary>Futures: the funding its parts paid or received, positive when received. 0 on Spot. Null when it couldn't be read.</summary>
    public decimal? Funding { get; set; }

    /// <summary>On <see cref="ExitQuantity"/>, net of <see cref="Fees"/> and <see cref="Funding"/>. A loss is negative. Null while open.</summary>
    public decimal? RealizedPnl { get; set; }

    /// <summary>
    /// <see cref="RealizedPnl"/> over the risk of <see cref="ExitQuantity"/> at <see cref="InitialStop"/>. Null when
    /// nothing was priced.
    /// </summary>
    public decimal? RMultiple { get; set; }

    /// <summary>The most the price went against the trade while it was open, times its quantity. Null when the candles couldn't be read.</summary>
    public decimal? AdverseExcursion { get; set; }

    /// <summary>The most the price went its way while it was open, times its quantity. Null when the candles couldn't be read.</summary>
    public decimal? FavorableExcursion { get; set; }

    /// <summary>How its last part ended.</summary>
    public TradeExit? ExitReason { get; set; }

    /// <summary>When its figures came from Binance. Until then they're the order rows'.</summary>
    public DateTimeOffset? SettledAt { get; set; }

    public int SettleAttempts { get; set; }

    /// <summary>
    /// When the entry's fills, and so its fees, were read while the trade was open, so its unrealized P&amp;L is net
    /// of them (FR-AGT-11). Null until then. Settling reads them again, whole.
    /// </summary>
    public DateTimeOffset? EntryFeesReadAt { get; set; }

    /// <summary>
    /// Futures: the time up to which funding was read while the trade was open. Null until the first read. Settling
    /// reads it again, whole, up to the close.
    /// </summary>
    public DateTimeOffset? FundingReadTo { get; set; }

    /// <summary>Settling isn't tried again before this, after a failure.</summary>
    public DateTimeOffset? SettleAfter { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One fill of a trade, SRS 6.1's Fill: of its entry, of one of its exits, or of a close made outside Ecnanib.</summary>
internal sealed class FillRow
{
    public required Guid Id { get; init; }

    public required Guid TradeId { get; init; }

    /// <summary>The order it filled. Null for an <see cref="FillSource.Outside"/> fill, which was the user's.</summary>
    public Guid? OrderId { get; init; }

    public required FillSource Source { get; init; }

    /// <summary>Binance's ID for the trade, unique in the pair.</summary>
    public required long ExchangeTradeId { get; init; }

    /// <summary>Binance's ID for the order it filled.</summary>
    public required long ExchangeOrderId { get; init; }

    public required ExchangeSide Side { get; init; }

    public required decimal Price { get; init; }

    /// <summary>
    /// What it filled, in the base asset. An outside fill holds only what was allocated to this trade, because one
    /// trade of the user's can close several parts.
    /// </summary>
    public required decimal Quantity { get; init; }

    /// <summary>The commission, in <see cref="FeeAsset"/>. An outside fill's is its share.</summary>
    public required decimal Fee { get; init; }

    public required string FeeAsset { get; init; }

    /// <summary>The commission in the trade's quote asset, at the time of the fill. Null when there was no way to convert it.</summary>
    public decimal? FeeQuote { get; init; }

    public required bool Maker { get; init; }

    public required DateTimeOffset FilledAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A trade's share of one Futures funding payment, SRS 6.1's FundingPayment.</summary>
internal sealed class FundingPaymentRow
{
    public required Guid Id { get; init; }

    public required Guid TradeId { get; init; }

    /// <summary>Binance's <c>tranId</c> for the payment.</summary>
    public required long ExchangeTransactionId { get; init; }

    public required DateTimeOffset PaidAt { get; init; }

    /// <summary>The funding rate at that time, as a fraction. Null when Binance's history didn't have it.</summary>
    public decimal? Rate { get; init; }

    /// <summary>The trade's share, positive when received, in <see cref="Asset"/>.</summary>
    public required decimal Amount { get; init; }

    /// <summary>The whole payment, which the agent's trades held at the time share by quantity.</summary>
    public required decimal PaymentAmount { get; init; }

    public required string Asset { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A decision cycle that led to an order of the trade: its entry, an add's stop, an exit or an adjustment.</summary>
internal sealed class TradeCycleRow
{
    public required Guid TradeId { get; init; }

    public required Guid CycleId { get; init; }
}

/// <summary>Stored by name, so a value is never renamed.</summary>
internal enum FillSource
{
    /// <summary>A fill of one of the agent's orders.</summary>
    Agent,

    /// <summary>A trade of the user's that closed part of the trade outside Ecnanib.</summary>
    Outside,
}

internal sealed class TradeRowConfiguration : IEntityTypeConfiguration<TradeRow>
{
    public void Configure(EntityTypeBuilder<TradeRow> builder)
    {
        builder.ToTable("trades");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Market).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Environment).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Side).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.ExitReason).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Symbol).HasMaxLength(OrderRow.SymbolMaxLength);
        builder.Property(row => row.BaseAsset).HasMaxLength(TradeRow.AssetMaxLength);
        builder.Property(row => row.QuoteAsset).HasMaxLength(TradeRow.AssetMaxLength);

        builder.HasIndex(row => row.EntryId).IsUnique();

        // The loss limits sum and count by close.
        builder.HasIndex(row => new { row.UserId, row.AgentId, row.ClosedAt });

        // What booking and the sweep look for. Written out, so the statuses are constants the queries match.
        builder.HasIndex(row => row.AgentId).HasDatabaseName("ix_trades_open").HasFilter("status = 'Open'");
        builder.HasIndex(row => row.SettleAfter).HasDatabaseName("ix_trades_unsettled").HasFilter("status = 'Closed' AND settled_at IS NULL");

        builder.HasMany<FillRow>().WithOne().HasForeignKey(fill => fill.TradeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany<FundingPaymentRow>().WithOne().HasForeignKey(payment => payment.TradeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany<TradeCycleRow>().WithOne().HasForeignKey(cycle => cycle.TradeId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FillRowConfiguration : IEntityTypeConfiguration<FillRow>
{
    public void Configure(EntityTypeBuilder<FillRow> builder)
    {
        builder.ToTable("fills");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Source).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Side).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.FeeAsset).HasMaxLength(TradeRow.AssetMaxLength);
        builder.HasIndex(row => new { row.TradeId, row.Source, row.ExchangeTradeId }).IsUnique();
        builder.HasIndex(row => row.OrderId);
    }
}

internal sealed class FundingPaymentRowConfiguration : IEntityTypeConfiguration<FundingPaymentRow>
{
    public void Configure(EntityTypeBuilder<FundingPaymentRow> builder)
    {
        builder.ToTable("funding_payments");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Asset).HasMaxLength(TradeRow.AssetMaxLength);
        builder.HasIndex(row => new { row.TradeId, row.ExchangeTransactionId }).IsUnique();
    }
}

internal sealed class TradeCycleRowConfiguration : IEntityTypeConfiguration<TradeCycleRow>
{
    public void Configure(EntityTypeBuilder<TradeCycleRow> builder)
    {
        builder.ToTable("trade_cycles");
        builder.HasKey(row => new { row.TradeId, row.CycleId });

        // Attribution (#43) and the trace viewer (#42) go from a cycle to its trades.
        builder.HasIndex(row => row.CycleId);
    }
}
