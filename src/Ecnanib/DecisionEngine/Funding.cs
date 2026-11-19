using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What a position of the Futures market received or paid at one funding time, as a row of the
/// <c>fundings</c> table: how much, in which asset, and when (FR-BIN-F4). See "Funding" in
/// docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// It is what the exchange said, and is stored once for its position: the number that the
/// exchange gave it is unique among the funding of an account. The funding of a position is its
/// rows together, as the fees of a position are the fees of its executions: nothing adds them up
/// here. What works out the equity of an agent adds them up, each time that it is asked for
/// it: see <see cref="AgentResults"/>.
/// </para>
/// <para>
/// It carries its owner along to the position that it hangs on: <c>(position_id, user_id)</c>
/// points at <c>positions (id, user_id)</c>. The position says the mode that the agent was in
/// (FR-BIN-11). See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="positionId">The position that received or paid it.</param>
/// <param name="userId">The user whose agent the position is of.</param>
/// <param name="transactionId">The number that the exchange gave it.</param>
/// <param name="amount">How much: above zero when the position received it, and below zero when the position paid it.</param>
/// <param name="asset">The asset that it was received or paid in.</param>
/// <param name="at">When, by the clock of the exchange.</param>
public sealed class Funding(
    Guid id, Guid positionId, Guid userId, string transactionId, decimal amount, string asset, DateTimeOffset at)
    : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid PositionId { get; } = positionId;

    public Guid UserId { get; } = userId;

    public string TransactionId { get; } = transactionId;

    public decimal Amount { get; } = amount;

    public string Asset { get; } = asset;

    public DateTimeOffset At { get; } = at;
}

/// <remarks>
/// It is named for the position, so that it is applied after the configuration of the position:
/// configurations are applied in the order of their names, and the names of the keys between the
/// two tables are made from the name that the table of the position has by then. Applied before
/// it, the keys would be named for a table <c>position</c>, and the key from the orders to the
/// positions would be renamed with them.
/// </remarks>
internal sealed class PositionFundingConfiguration : IEntityTypeConfiguration<Funding>
{
    public void Configure(EntityTypeBuilder<Funding> builder)
    {
        builder.ToTable("fundings");
        builder.HasKey(funding => funding.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(funding => funding.Id).ValueGeneratedNever();
        builder.Property(funding => funding.PositionId);
        builder.Property(funding => funding.UserId);
        builder.Property(funding => funding.TransactionId);
        builder.Property(funding => funding.Amount);
        builder.Property(funding => funding.Asset);
        builder.Property(funding => funding.At);

        // What a position received or paid at a funding time is stored once. The index also
        // finds the funding of a position.
        builder.HasIndex(funding => new { funding.PositionId, funding.TransactionId }).IsUnique();

        // The funding of a position goes with it, which goes with its agent, and with the
        // account when that is deleted. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Position>()
            .WithMany()
            .HasForeignKey(funding => new { funding.PositionId, funding.UserId })
            .HasPrincipalKey(position => new { position.Id, position.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
