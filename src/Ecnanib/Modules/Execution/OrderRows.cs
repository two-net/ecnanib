using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// One order an agent sent, or is about to send: SRS 6.1's Order. A row is written before its order is sent, so a
/// send that's cut off can be settled by its client order ID and is never sent twice (FR-EXE-05, NFR-REL-03). Prices
/// are in the quote asset and quantities in the base asset of <see cref="Symbol"/> (DATA-R-02). There's no foreign key
/// to agents, users or cycles, which other modules own: deleting an agent reaches this table through
/// <c>IAgentHistory</c>. See Order execution in docs/architecture.md.
/// </summary>
internal sealed class OrderRow
{
    public const int EnumMaxLength = 32;

    public const int SymbolMaxLength = 20;

    public const int ClientOrderIdMaxLength = 36;

    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required Guid AgentId { get; init; }

    /// <summary>
    /// The cycle whose passing evaluation the order cites (AC-01). A stop, target or close cites its entry's cycle,
    /// and a <see cref="OrderOrigin.System"/> close carries its own evaluation too.
    /// </summary>
    public required Guid CycleId { get; init; }

    public required OrderOrigin Origin { get; init; }

    /// <summary>The candle close of <see cref="CycleId"/>, which client order IDs are derived from (FR-EXE-04).</summary>
    public required DateTimeOffset CandleClose { get; init; }

    /// <summary>A System order's own passing evaluation, as JSON, which the AC-01 audit query reads.</summary>
    public string? SystemEvaluation { get; init; }

    public required Guid CredentialId { get; init; }

    public required Market Market { get; init; }

    public required TradingEnvironment Environment { get; init; }

    public required string Symbol { get; init; }

    public required OrderLeg Leg { get; init; }

    /// <summary>The entry a stop, target or close belongs to.</summary>
    public Guid? EntryId { get; init; }

    /// <summary>The protection a stop, target or close belongs to.</summary>
    public Guid? ProtectionId { get; init; }

    /// <summary>The Spot OCO list a stop or target belongs to.</summary>
    public string? ListClientOrderId { get; init; }

    /// <summary>Unique, derived from the agent, the cycle and the leg (FR-EXE-04). See <see cref="ClientOrderIds"/>.</summary>
    public required string ClientOrderId { get; init; }

    public long? ExchangeOrderId { get; set; }

    public required OrderKind Type { get; init; }

    public required OrderSide Side { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>A limit price: a limit entry's, or a Spot target's.</summary>
    public decimal? Price { get; init; }

    /// <summary>A stop's or a Futures target's trigger price.</summary>
    public decimal? StopPrice { get; init; }

    public bool ReduceOnly { get; init; }

    /// <summary>An entry's protective stop, from its <c>SizedOrder</c>, which every protection of it uses.</summary>
    public decimal? ProtectiveStop { get; init; }

    /// <summary>An entry's take-profit, from its <c>SizedOrder</c>, when it has one (FR-EXE-03).</summary>
    public decimal? Target { get; init; }

    public OrderStatus Status { get; set; }

    public decimal ExecutedQuantity { get; set; }

    public decimal? AveragePrice { get; set; }

    /// <summary>
    /// An entry's: what the position holds from it. That's the executed quantity, less on Spot the commission Binance
    /// took from the base asset, so a Spot agent never sells more than its own fills gave it (FR-AGT-09).
    /// </summary>
    public decimal? NetQuantity { get; set; }

    /// <summary>The executed quantity <see cref="NetQuantity"/> was worked out from, so it's worked out again after another fill.</summary>
    public decimal? NetBasis { get; set; }

    /// <summary>
    /// An entry's: it has ended, and every part of it that filled has a protection, or is too small for one. Until
    /// then, it's watched (FR-EXE-02).
    /// </summary>
    public bool Resolved { get; set; }

    /// <summary>Binance's code when it rejected the order.</summary>
    public int? ErrorCode { get; set; }

    /// <summary>
    /// Until then, a send with this client order ID could still be accepted, so a query that finds nothing doesn't
    /// prove it was never placed, and nothing is sent again (FR-EXE-05). Written before the send, so it holds after a
    /// restart.
    /// </summary>
    public required DateTimeOffset SettlesAt { get; set; }

    /// <summary>A limit entry's: when it's cancelled if it hasn't filled (FR-EXE-01).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When Binance last reported a fill of it, which a stop must follow within seconds (AC-02).</summary>
    public DateTimeOffset? FilledAt { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? SentAt { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsLive => Status is OrderStatus.Sending or OrderStatus.Unknown or OrderStatus.Open or OrderStatus.PartiallyFilled
        or OrderStatus.Triggered;

    /// <summary>Its outcome isn't known yet: it's being sent, or the send got no usable answer.</summary>
    public bool IsUnsettled => Status is OrderStatus.Sending or OrderStatus.Unknown;
}

/// <summary>
/// What protects one filled part of an entry: its stop, its target if any, and the close at market if the stop can't
/// be placed in time (FR-EXE-02). An entry that fills at once has one. A limit entry that fills in parts gets one for
/// each part, so nothing that filled waits for the rest. See Order execution in docs/architecture.md.
/// </summary>
internal sealed class ProtectionRow
{
    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required Guid AgentId { get; init; }

    public required Guid EntryId { get; init; }

    /// <summary>The order of the parts, from 0, which client order IDs include.</summary>
    public required int Sequence { get; init; }

    /// <summary>The base asset it protects, on the stop's quantity grid.</summary>
    public required decimal Quantity { get; init; }

    public ProtectionStatus Status { get; set; }

    /// <summary>When its stop must rest on Binance, or what it protects is closed at market (AC-02).</summary>
    public required DateTimeOffset ProtectBy { get; set; }

    /// <summary>Binance refused the target, so only a stop protects it (FR-EXE-03).</summary>
    public bool TargetDropped { get; set; }

    /// <summary>The attempts at a stop so far, which client order IDs include.</summary>
    public int Attempts { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Stored by name, so a value is never renamed.</summary>
internal enum OrderOrigin
{
    /// <summary>From a cycle's passing evaluation of the council's proposal.</summary>
    Council,

    /// <summary>Ecnanib's own, such as the close when a stop can't be placed, with an evaluation of its own.</summary>
    System,
}

/// <summary>Stored by name, so a value is never renamed.</summary>
internal enum OrderLeg
{
    Entry,
    Stop,
    Target,

    /// <summary>The close at market when a stop couldn't be placed (FR-EXE-02).</summary>
    EmergencyClose,
}

/// <summary>Stored by name, so a value is never renamed.</summary>
internal enum OrderKind
{
    Market,
    Limit,

    /// <summary>Spot STOP_LOSS, or Futures STOP_MARKET through the algo order service.</summary>
    StopMarket,

    /// <summary>Futures TAKE_PROFIT_MARKET through the algo order service.</summary>
    TakeProfitMarket,

    /// <summary>A Spot OCO's target.</summary>
    LimitMaker,
}

/// <summary>Stored by name, so a value is never renamed.</summary>
internal enum OrderStatus
{
    /// <summary>Written, and being sent. One left behind by a crash is settled like <see cref="Unknown"/>.</summary>
    Sending,

    /// <summary>Sent with no usable answer. Settled by its client order ID, not before <see cref="OrderRow.SettlesAt"/>.</summary>
    Unknown,

    /// <summary>Never reached Binance: not sent, refused before sending, or shown missing once it had settled.</summary>
    NotPlaced,

    Open,
    PartiallyFilled,
    Filled,

    /// <summary>A Futures conditional order whose trigger was met.</summary>
    Triggered,

    /// <summary>A Futures conditional order that triggered, and whose order then ended.</summary>
    Finished,

    Canceled,
    Expired,
    Rejected,
}

/// <summary>Stored by name, so a value is never renamed.</summary>
internal enum ProtectionStatus
{
    /// <summary>No stop rests yet.</summary>
    Pending,

    /// <summary>A stop rests on Binance.</summary>
    Resting,

    /// <summary>The stop couldn't be placed in time, so what it protects is being closed at market.</summary>
    Closing,

    /// <summary>Closed at market.</summary>
    Closed,

    /// <summary>Too small for Binance to take a stop or a close, so nothing can be done with it.</summary>
    Dust,

    /// <summary>
    /// Binance rejected every close, so Ecnanib has stopped trying, and the user must close what's left. Logged as
    /// Critical.
    /// </summary>
    Abandoned,

    /// <summary>
    /// What it protected is gone: its stop or target executed, or the position was found closed and its orders were
    /// cancelled. Reconciliation (#35) records how.
    /// </summary>
    Ended,
}

internal sealed class OrderRowConfiguration : IEntityTypeConfiguration<OrderRow>
{
    public void Configure(EntityTypeBuilder<OrderRow> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Origin).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Market).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Environment).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Leg).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Type).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Side).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.Symbol).HasMaxLength(OrderRow.SymbolMaxLength);
        builder.Property(row => row.ClientOrderId).HasMaxLength(OrderRow.ClientOrderIdMaxLength);
        builder.Property(row => row.ListClientOrderId).HasMaxLength(OrderRow.ClientOrderIdMaxLength);
        builder.Property(row => row.SystemEvaluation).HasColumnType("jsonb");
        builder.Ignore(row => row.IsLive);
        builder.Ignore(row => row.IsUnsettled);

        builder.HasIndex(row => row.ClientOrderId).IsUnique();
        builder.HasIndex(row => new { row.UserId, row.AgentId, row.Status });
        builder.HasIndex(row => row.EntryId);
        builder.HasIndex(row => row.AgentId).HasDatabaseName("ix_orders_unresolved_entries").HasFilter("leg = 'Entry' AND NOT resolved");

        // What the sweep looks for: orders whose outcome or fill isn't settled yet.
        builder.HasIndex(row => row.Status)
            .HasDatabaseName("ix_orders_live")
            .HasFilter("status IN ('Sending', 'Unknown', 'Open', 'PartiallyFilled')");
    }
}

internal sealed class ProtectionRowConfiguration : IEntityTypeConfiguration<ProtectionRow>
{
    public void Configure(EntityTypeBuilder<ProtectionRow> builder)
    {
        builder.ToTable("protections");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.HasIndex(row => new { row.EntryId, row.Sequence }).IsUnique();
        builder.HasIndex(row => new { row.UserId, row.AgentId, row.Status });

        builder.HasIndex(row => row.Status)
            .HasDatabaseName("ix_protections_unsettled")
            .HasFilter("status IN ('Pending', 'Closing')");
    }
}
