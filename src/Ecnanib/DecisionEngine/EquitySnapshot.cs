using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The equity of an agent at a moment, as a row of the <c>equity_snapshots</c> table: the capital
/// that its user allocated to it, plus its net profit or loss to date. The rows of an agent are
/// its equity over time. See "The equity of an agent over time" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the equity that the loss limits of the agent were evaluated on,</b> with unrealized
/// profit and loss, and with the fees and the funding that the exchange said (FR-BIN-10). What
/// watches the loss limits writes it, in the transaction of an evaluation: see
/// <see cref="EquitySnapshots"/>. Nothing is read from the exchange for it.
/// </para>
/// <para>
/// <b>It says the capital beside the equity.</b> An equity that rises because its user allocated
/// more is no profit: what the agent made is the equity less the capital.
/// </para>
/// <para>
/// <b>An agent has rows for each mode that it traded in.</b> What it did in Demo is never
/// combined with what it does in Live (FR-BIN-11). And each row says the asset that it is in: the
/// equity of an agent that was given a symbol which is priced in another asset is in that asset
/// from then on.
/// </para>
/// <para>
/// It carries its owner along to the agent that it hangs on, as a position does:
/// <c>(agent_id, user_id)</c> points at <c>agents (id, user_id)</c>. See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="agentId">The agent whose equity it is.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="mode">The mode that the equity is of.</param>
/// <param name="at">The moment.</param>
/// <param name="asset">The asset that the amounts are in: the one that the symbol of the agent was priced in then.</param>
/// <param name="capital">The capital that the agent was allocated then.</param>
/// <param name="equity">Its equity then: the capital, plus its net profit or loss to date, with unrealized profit and loss.</param>
public sealed class EquitySnapshot(
    Guid agentId, Guid userId, TradingMode mode, DateTimeOffset at, string asset, decimal capital, decimal equity) : IUserOwned
{
    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public TradingMode Mode { get; } = mode;

    public DateTimeOffset At { get; } = at;

    public string Asset { get; } = asset;

    public decimal Capital { get; } = capital;

    public decimal Equity { get; } = equity;
}

internal sealed class EquitySnapshotConfiguration : IEntityTypeConfiguration<EquitySnapshot>
{
    public void Configure(EntityTypeBuilder<EquitySnapshot> builder)
    {
        builder.ToTable("equity_snapshots");

        // The equity of an agent in a mode at a moment, once. The index of the key gives the
        // rows of an agent in the order of time, and the newest of them.
        builder.HasKey(snapshot => new { snapshot.AgentId, snapshot.Mode, snapshot.At });

        builder.Property(snapshot => snapshot.AgentId);
        builder.Property(snapshot => snapshot.UserId);
        builder.Property(snapshot => snapshot.Mode);
        builder.Property(snapshot => snapshot.At);
        builder.Property(snapshot => snapshot.Asset);
        builder.Property(snapshot => snapshot.Capital);
        builder.Property(snapshot => snapshot.Equity);

        // The equity of an agent goes with it when it is deleted, and with the account when that
        // is. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(snapshot => new { snapshot.AgentId, snapshot.UserId })
            .HasPrincipalKey(agent => new { agent.Id, agent.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// When the equity of an agent is recorded: the one place that says so, for what watches the
/// loss limits, which writes the rows.
/// </summary>
internal static class EquitySnapshots
{
    /// <summary>
    /// The time in which the equity of an agent that holds a position is recorded once at most:
    /// the length of the shortest candle, at the close of which the cycles start. A price moves
    /// such an equity with every evaluation, and one row for each would be many.
    /// </summary>
    public static readonly TimeSpan Every = Timeframe.FifteenMinutes.Length();

    /// <summary>The start of the time of <see cref="Every"/> that a moment is in.</summary>
    public static DateTimeOffset SlotOf(DateTimeOffset moment) => new(moment.UtcTicks - (moment.UtcTicks % Every.Ticks), TimeSpan.Zero);

    /// <summary>
    /// Whether the equity of an agent is recorded now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An equity that is as the newest row says is not recorded again.</b> An agent that
    /// trades nothing has the equity that it had: its curve holds the value of its newest row.
    /// </para>
    /// <para>
    /// <b>Every change of a settled equity is recorded.</b> An agent that holds no position has
    /// an equity that changes when something is stored for it, a fee or funding, or when its
    /// user changes its capital: seldom, and each is a row.
    /// </para>
    /// <para>
    /// <b>An equity that a price moves is recorded once in <see cref="Every"/>.</b>
    /// </para>
    /// </remarks>
    /// <param name="newest">The newest row of the agent in its mode, or null when it has none.</param>
    /// <param name="asset">The asset that the symbol of the agent is priced in now.</param>
    /// <param name="capital">The capital that the agent is allocated now.</param>
    /// <param name="equity">Its equity now.</param>
    /// <param name="holds">Whether the agent holds an open position, whose price moves its equity.</param>
    /// <param name="now">The moment.</param>
    public static bool IsDue(EquitySnapshot? newest, string asset, decimal capital, decimal equity, bool holds, DateTimeOffset now)
    {
        if (newest is null)
        {
            return true;
        }

        // A row of a later moment than the clock shows stays the newest: a clock that was set
        // back writes nothing before it.
        if (newest.At >= now || (newest.Asset == asset && newest.Capital == capital && newest.Equity == equity))
        {
            return false;
        }

        return !holds || newest.At < SlotOf(now);
    }
}
