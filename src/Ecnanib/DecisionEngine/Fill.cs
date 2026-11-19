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
/// <b>What the fee is worth in the asset that the symbol is priced in is said beside it</b>
/// (FR-BIN-10), once, as the execution is stored: every profit and loss figure counts the fee
/// by that. A fee in the asset that the symbol is priced in is what it is. One in the asset that
/// is traded is worth what its execution paid for it. One in any other asset, such as BNB, is
/// worth what that asset traded at then, where the configuration file names the symbol that
/// prices it. It says nothing while that could not be read: what settles the orders values it
/// later, and a fee that is never valued is in no figure. See <see cref="FeeValues"/> and "The
/// value of a fee" in docs/orders.md.
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
/// <param name="feeValue">
/// What the fee is worth in the asset that the symbol of its order is priced in, or null while
/// that is not known.
/// </param>
public sealed class Fill(
    Guid id,
    Guid orderId,
    Guid userId,
    string tradeId,
    decimal price,
    decimal quantity,
    decimal fee,
    string feeAsset,
    DateTimeOffset at,
    decimal? feeValue = null)
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

    public decimal? FeeValue { get; } = feeValue;
}

/// <summary>
/// What the fee of an execution is worth in the asset that the symbol of its order is priced in
/// (FR-BIN-10): the one place that says so, for an execution as it is stored and for one that is
/// valued later.
/// </summary>
internal static class FeeValues
{
    /// <summary>
    /// The symbol that prices a fee which was taken in neither asset of the symbol that was
    /// traded: the asset of the fee, priced in the asset that the traded symbol is priced in.
    /// Null for a fee that needs no price of another symbol.
    /// </summary>
    /// <param name="feeAsset">The asset that the fee was taken in.</param>
    /// <param name="baseAsset">The asset that is traded.</param>
    /// <param name="quoteAsset">The asset that the traded symbol is priced in.</param>
    public static string? PricedBy(string feeAsset, string baseAsset, string quoteAsset) =>
        feeAsset == quoteAsset || feeAsset == baseAsset ? null : feeAsset + quoteAsset;

    /// <summary>
    /// What a fee is worth in the asset that the traded symbol is priced in, or null when it is
    /// in another asset and that asset has no price.
    /// </summary>
    /// <param name="fee">The fee.</param>
    /// <param name="feeAsset">The asset that it was taken in.</param>
    /// <param name="price">The price of its execution.</param>
    /// <param name="baseAsset">The asset that is traded.</param>
    /// <param name="quoteAsset">The asset that the traded symbol is priced in.</param>
    /// <param name="feeAssetPrice">What the asset of the fee trades at in the asset that the symbol is priced in, or null when that is not known.</param>
    public static decimal? Of(decimal fee, string feeAsset, decimal price, string baseAsset, string quoteAsset, decimal? feeAssetPrice)
    {
        if (feeAsset == quoteAsset)
        {
            return AgentResults.Rounded(fee);
        }

        try
        {
            // In the asset that is traded, a fee is worth what its execution paid for it.
            return feeAsset == baseAsset
                ? AgentResults.Rounded(fee * price)
                : feeAssetPrice is { } worth ? AgentResults.Rounded(fee * worth) : null;
        }
        catch (OverflowException)
        {
            // Numbers that cannot be worked out with are no numbers of a market: the fee has no value.
            return null;
        }
    }
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
        builder.Property(fill => fill.FeeValue);

        // An execution is stored once. The index also finds the executions of an order.
        builder.HasIndex(fill => new { fill.OrderId, fill.TradeId }).IsUnique();

        // The executions whose fee is still to be valued, which what settles the orders asks
        // for with every run: few of all the executions that there ever were.
        builder.HasIndex(fill => fill.OrderId).HasFilter("fee_value IS NULL");

        // The executions of an order go with it, which goes with its agent, and with the account
        // when that is deleted. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(fill => new { fill.OrderId, fill.UserId })
            .HasPrincipalKey(order => new { order.Id, order.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
