using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The result of a closed position, as a row of the <c>position_results</c> table: its net profit
/// or loss, its result in R, its fees, its funding, and how long it was held (FR-EXP-1,
/// FR-BIN-10). See "The results of a position" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is written once, and never changed.</b> A position has its result when it is closed and
/// all of it is stored: no order of it is with the exchange still, the executions of its orders
/// are stored with what their fees are worth, and its funding is read. From then on nothing of
/// it changes. A position of which something could not be read is given its result when it is
/// no longer asked for, with what there is, and says that it is not complete. See
/// <see cref="PositionResults"/>.
/// </para>
/// <para>
/// <b>What else is recorded of a closed position is the position's own.</b> How it was closed,
/// when it was opened and closed, and its agent, market, timeframe, symbol, and mode are on its
/// row, and the model that held each role is rows of <see cref="PositionRole"/>. Nothing of them
/// is said twice.
/// </para>
/// <para>
/// <b>R is the amount that was risked on the trade:</b> the distance from the entry to the
/// stop-loss, times the quantity, as they were when the position was opened. The entry of the
/// position keeps all three: the stop-loss of a position is moved, and what it holds grows less
/// as it is closed in parts.
/// </para>
/// <para>
/// It carries its owner along to the position that it hangs on: <c>(position_id, user_id)</c>
/// points at <c>positions (id, user_id)</c>. The position says the mode that the agent was in,
/// so a result in Demo is a result in Demo for good (FR-BIN-11). See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="positionId">The position whose result it is: a position has one.</param>
/// <param name="userId">The user whose agent the position is of.</param>
/// <param name="asset">The asset that the amounts are in: the one that the symbol of the position is priced in.</param>
/// <param name="grossResult">
/// What the position made before fees and funding: what each order that closed it, or a part of
/// it, was executed at against the price that it was entered at, with what left it by no order
/// of the agent.
/// </param>
/// <param name="fees">The fees of the executions of its orders, its entry among them, as far as they are valued. A sum of costs: above zero.</param>
/// <param name="funding">What it received as funding, less what it paid: above zero when it received more.</param>
/// <param name="netResult">Its net profit or loss: what it made, less the fees, with the funding.</param>
/// <param name="risk">R: the amount that was risked, or null where the entry does not say it.</param>
/// <param name="resultInR">The net result as a multiple of R, or null where R is not known or is nothing.</param>
/// <param name="holdingSeconds">How long it was held, in whole seconds: from the first execution of its entry to the last execution that closed it, by the clock of the exchange where the executions say it.</param>
/// <param name="complete">
/// Whether all of it is what the exchange said. It is not where executions or funding could not
/// be read in time, where a fee has no value, where an order that closed a part of it says no
/// price, where funding is in another asset, and where a part of the position left it by no
/// order of the agent, which is counted at the price when that was found.
/// </param>
/// <param name="recordedAt">When the row was written.</param>
public sealed class PositionResult(
    Guid positionId,
    Guid userId,
    string asset,
    decimal grossResult,
    decimal fees,
    decimal funding,
    decimal netResult,
    decimal? risk,
    decimal? resultInR,
    long holdingSeconds,
    bool complete,
    DateTimeOffset recordedAt) : IUserOwned
{
    public Guid PositionId { get; } = positionId;

    public Guid UserId { get; } = userId;

    public string Asset { get; } = asset;

    public decimal GrossResult { get; } = grossResult;

    public decimal Fees { get; } = fees;

    public decimal Funding { get; } = funding;

    public decimal NetResult { get; } = netResult;

    public decimal? Risk { get; } = risk;

    public decimal? ResultInR { get; } = resultInR;

    public long HoldingSeconds { get; } = holdingSeconds;

    public bool Complete { get; } = complete;

    public DateTimeOffset RecordedAt { get; } = recordedAt;

    /// <summary>How long the position was held.</summary>
    public TimeSpan HoldingTime => TimeSpan.FromSeconds(HoldingSeconds);
}

/// <remarks>It is named for the position, so that it is applied after the configuration of the position: see <see cref="PositionFundingConfiguration"/>.</remarks>
internal sealed class PositionResultConfiguration : IEntityTypeConfiguration<PositionResult>
{
    public void Configure(EntityTypeBuilder<PositionResult> builder)
    {
        builder.ToTable("position_results");

        // A position has one result: the row is found by the position, and is written once.
        builder.HasKey(result => result.PositionId);

        builder.Property(result => result.PositionId).ValueGeneratedNever();
        builder.Property(result => result.UserId);
        builder.Property(result => result.Asset);
        builder.Property(result => result.GrossResult);
        builder.Property(result => result.Fees);
        builder.Property(result => result.Funding);
        builder.Property(result => result.NetResult);
        builder.Property(result => result.Risk);
        builder.Property(result => result.ResultInR);
        builder.Property(result => result.HoldingSeconds);
        builder.Property(result => result.Complete);
        builder.Property(result => result.RecordedAt);
        builder.Ignore(result => result.HoldingTime);

        // The result of a position goes with it, which goes with its agent, and with the account
        // when that is deleted. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Position>()
            .WithMany()
            .HasForeignKey(result => new { result.PositionId, result.UserId })
            .HasPrincipalKey(position => new { position.Id, position.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
