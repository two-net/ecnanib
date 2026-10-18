using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// An order placed (or simulated in paper mode) for an agent (FR-EXE-01..03). <see cref="ClientOrderId"/> is unique
/// so retries never duplicate an order (FR-EXE-07, NFR-REL-02).
/// </summary>
public sealed class Order
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }

    public Agent Agent { get; set; } = null!;

    public Guid? DecisionId { get; set; }

    public Decision? Decision { get; set; }

    public Guid? TradeId { get; set; }

    public Trade? Trade { get; set; }

    /// <summary>Deterministic, at most 36 characters (FR-EXE-07).</summary>
    public required string ClientOrderId { get; set; }

    public long? ExchangeOrderId { get; set; }

    /// <summary>Spot OCO list that owns this leg (FR-EXE-01).</summary>
    public long? OrderListId { get; set; }

    public string? ListClientOrderId { get; set; }

    public OrderPurpose Purpose { get; set; }

    public OrderType Type { get; set; }

    public OrderSide Side { get; set; }

    public decimal? Price { get; set; }

    public decimal? StopPrice { get; set; }

    public decimal Quantity { get; set; }

    public decimal FilledQuantity { get; set; }

    public decimal? AverageFillPrice { get; set; }

    public OrderStatus Status { get; set; }

    public bool ReduceOnly { get; set; }

    public bool IsPaper { get; set; }

    /// <summary>Entry timeout after which an unfilled limit order is cancelled (FR-EXE-03).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? FilledAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint Version { get; set; }
}
