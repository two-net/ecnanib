using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;
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
    /// The cycle whose passing evaluation the order cites (AC-01). A stop, target or close cites its protection's
    /// <see cref="ProtectionRow.CycleId"/>: its entry's, or the add, exit or adjustment that changed it. A
    /// <see cref="OrderOrigin.System"/> close carries its own evaluation too.
    /// </summary>
    public required Guid CycleId { get; init; }

    public required OrderOrigin Origin { get; init; }

    /// <summary>
    /// The candle close its client order ID is derived from (FR-EXE-04): its cycle's, or for a stop or target, its
    /// protection's <see cref="ProtectionRow.IdCandleClose"/>.
    /// </summary>
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

    /// <summary>
    /// A Futures stop's or target's: Binance's ID for the order it became when it triggered, whose fills the trade
    /// journal reads. <see cref="ExchangeOrderId"/> is the algo order's.
    /// </summary>
    public long? TriggeredOrderId { get; set; }

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

    /// <summary>The attempts at a stop so far under <see cref="IdCandleClose"/>, which client order IDs include.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// The stop its part should have, which its stop legs are brought in line with. One stop guards the whole
    /// position: an add or an adjustment moves it, never wider (FR-RISK-06).
    /// </summary>
    public required decimal StopPrice { get; set; }

    /// <summary>Its take-profit, or null for none (FR-EXE-03). <see cref="TargetDropped"/> says Binance refused it.</summary>
    public decimal? TargetPrice { get; set; }

    /// <summary>What its part cost, per unit of the base asset: its entry's average price.</summary>
    public required decimal EntryPrice { get; init; }

    /// <summary>When its part filled. The position opened at the earliest of its parts.</summary>
    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>
    /// The cycle whose passing evaluation its stops, targets and closes cite (AC-01): its entry's, or that of the
    /// add, exit or adjustment that last changed it.
    /// </summary>
    public required Guid CycleId { get; set; }

    /// <summary>
    /// The candle close its legs' client order IDs are derived from (FR-EXE-04): its entry's, or that of the exit or
    /// adjustment that last rebased it. See <see cref="ClientOrderIds"/>.
    /// </summary>
    public required DateTimeOffset IdCandleClose { get; set; }

    /// <summary>The index its legs' client order IDs carry: its sequence, or its place among a rebasing cycle's.</summary>
    public required int IdIndex { get; set; }

    /// <summary>A stop of it has rested on Binance, so the next one to rest replaces it rather than opening a trade.</summary>
    public bool Rested { get; set; }

    /// <summary>When reconciliation found its stop cancelled outside Ecnanib while its part was still held.</summary>
    public DateTimeOffset? StopLostAt { get; set; }

    /// <summary>Why it's <see cref="ProtectionStatus.Closing"/>.</summary>
    public ProtectionClosing? ClosingFor { get; set; }

    /// <summary>How it ended, once it's <see cref="ProtectionStatus.Closed"/> or <see cref="ProtectionStatus.Ended"/>.</summary>
    public ProtectionEnd? EndReason { get; set; }

    /// <summary>
    /// When it stopped being the agent's to look after: <see cref="ProtectionStatus.Closed"/>,
    /// <see cref="ProtectionStatus.Ended"/>, <see cref="ProtectionStatus.Dust"/> or <see cref="ProtectionStatus.Abandoned"/>.
    /// Dust and Abandoned parts from before #36 have none.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

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
    /// What it protected is gone: its stop or target executed, it was closed outside Ecnanib, it was left to the
    /// user, or it was split into smaller protections. <see cref="ProtectionRow.EndReason"/> says which.
    /// </summary>
    Ended,
}

/// <summary>Why a protection is being closed at market. Stored by name, so a value is never renamed.</summary>
internal enum ProtectionClosing
{
    /// <summary>Its stop couldn't be placed in time, so it's closed under an evaluation of Ecnanib's own (FR-EXE-02).</summary>
    Unprotected,

    /// <summary>An exit the council proposed, under that cycle's evaluation.</summary>
    Exit,

    /// <summary>The kill switch stopped the agent (FR-AGT-08), so it's closed under an evaluation of Ecnanib's own.</summary>
    KillSwitch,
}

/// <summary>How a protection ended. Stored by name, so a value is never renamed.</summary>
internal enum ProtectionEnd
{
    /// <summary>Its stop executed on Binance.</summary>
    StopFilled,

    /// <summary>Its take-profit executed on Binance.</summary>
    TargetFilled,

    /// <summary>An exit closed it at market.</summary>
    Exited,

    /// <summary>Its stop couldn't be placed, so it was closed at market (FR-EXE-02).</summary>
    Unprotected,

    /// <summary>What it held was closed outside Ecnanib, such as by the user on Binance (FR-EXE-06).</summary>
    ClosedOutside,

    /// <summary>
    /// Its stop was cancelled outside Ecnanib after the agent was stopped, which leaves what it holds as it is
    /// (FR-AGT-07), so the user now manages it.
    /// </summary>
    LeftToUser,

    /// <summary>Replaced by smaller protections, which together hold what it did.</summary>
    Split,

    /// <summary>The kill switch closed it at market (FR-AGT-08).</summary>
    KillSwitch,
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

        // The trace viewer lists a cycle's orders.
        builder.HasIndex(row => row.CycleId);

        builder.HasIndex(row => row.AgentId).HasDatabaseName("ix_orders_unresolved_entries").HasFilter("leg = 'Entry' AND NOT resolved");

        // What the sweeps look for: orders whose outcome or fill isn't settled yet, or that may still rest.
        builder.HasIndex(row => row.Status)
            .HasDatabaseName("ix_orders_live")
            .HasFilter("status IN ('Sending', 'Unknown', 'Open', 'PartiallyFilled', 'Triggered')");
    }
}

internal sealed class ProtectionRowConfiguration : IEntityTypeConfiguration<ProtectionRow>
{
    public void Configure(EntityTypeBuilder<ProtectionRow> builder)
    {
        builder.ToTable("protections");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.ClosingFor).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.Property(row => row.EndReason).HasConversion<string>().HasMaxLength(OrderRow.EnumMaxLength);
        builder.HasIndex(row => new { row.EntryId, row.Sequence }).IsUnique();
        builder.HasIndex(row => new { row.UserId, row.AgentId, row.Status });

        builder.HasIndex(row => row.Status)
            .HasDatabaseName("ix_protections_unsettled")
            .HasFilter("status IN ('Pending', 'Closing')");

        // What reconciliation's sweep looks for: parts of a position that may still be held.
        builder.HasIndex(row => row.AgentId)
            .HasDatabaseName("ix_protections_held")
            .HasFilter("status IN ('Pending', 'Resting', 'Closing')");
    }
}
