using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// One execution of an order, as a row of the <c>fills</c> table: its price, its quantity, the
/// fee that the exchange took for it, and when. See "The record of an order" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// It is what the exchange said, and is stored once: the number that the exchange gave the
/// execution is unique among the executions of its order. The fee is in the asset that the
/// exchange took it in, which on the Spot market may be the asset that was bought: what the
/// agent then holds is less than what was executed.
/// </para>
/// <para>
/// It carries its owner along to the order that it hangs on: <c>(order_id, user_id)</c> points
/// at <c>orders (id, user_id)</c>. The order says the mode that the agent was in (FR-BIN-11). See
/// docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="orderId">The order that was executed.</param>
/// <param name="userId">The user whose agent the order is of.</param>
/// <param name="tradeId">The number that the exchange gave the execution.</param>
/// <param name="price">The price that it was executed at.</param>
/// <param name="quantity">How much of the asset that is traded was executed.</param>
/// <param name="fee">The fee that the exchange took for it.</param>
/// <param name="feeAsset">The asset that the fee was taken in.</param>
/// <param name="at">When it was executed.</param>
public sealed class Fill(
    Guid id, Guid orderId, Guid userId, string tradeId, decimal price, decimal quantity, decimal fee, string feeAsset, DateTimeOffset at)
    : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid OrderId { get; } = orderId;

    public Guid UserId { get; } = userId;

    public string TradeId { get; } = tradeId;

    public decimal Price { get; } = price;

    public decimal Quantity { get; } = quantity;

    public decimal Fee { get; } = fee;

    public string FeeAsset { get; } = feeAsset;

    public DateTimeOffset At { get; } = at;
}

internal sealed class FillConfiguration : IEntityTypeConfiguration<Fill>
{
    public void Configure(EntityTypeBuilder<Fill> builder)
    {
        builder.ToTable("fills");
        builder.HasKey(fill => fill.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(fill => fill.Id).ValueGeneratedNever();
        builder.Property(fill => fill.OrderId);
        builder.Property(fill => fill.UserId);
        builder.Property(fill => fill.TradeId);
        builder.Property(fill => fill.Price);
        builder.Property(fill => fill.Quantity);
        builder.Property(fill => fill.Fee);
        builder.Property(fill => fill.FeeAsset);
        builder.Property(fill => fill.At);

        // An execution is stored once. The index also finds the executions of an order.
        builder.HasIndex(fill => new { fill.OrderId, fill.TradeId }).IsUnique();

        // The executions of an order go with it, which goes with its agent, and with the account
        // when that is deleted. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(fill => new { fill.OrderId, fill.UserId })
            .HasPrincipalKey(order => new { order.Id, order.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
