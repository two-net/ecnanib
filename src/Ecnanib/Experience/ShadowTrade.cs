using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Experience;

/// <summary>
/// The shadow trade of a vetoed plan, as a row of the <c>shadow_trades</c> table: what the plan
/// that a Risk Manager vetoed would have produced, against the candles that closed after it
/// (FR-EXP-5). See "Shadow trades" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never places an order.</b> It is a row, and a rule that reads candles:
/// <see cref="ShadowTradeRule"/>. Experience cannot reach the orders of the Exchange Gateway.
/// </para>
/// <para>
/// <b>It is written with its cycle, and ended once.</b> What records a cycle that ended with a
/// veto writes the row with the prices of the plan, and with no outcome. A job follows it over
/// the candles that close after it, and gives it its outcome and its result once it has ended.
/// Until then only <see cref="DueAt"/> changes, which is when it is next looked at.
/// </para>
/// <para>
/// <b>It says what its cycle says, in columns of its own:</b> the mode, the market, the symbol,
/// and the timeframe of the agent when the cycle ran, and the model that reviewed the plan. What
/// an agent did in Demo stays Demo (FR-BIN-11).
/// </para>
/// <para>
/// It carries its owner along to the cycle that it hangs on: <c>(cycle_id, user_id)</c> points
/// at <c>decision_cycles (id, user_id)</c>, which the Decision Engine declares, because
/// Experience may not use it. So the row goes with its cycle, with the agent of that cycle, and
/// with the account. See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="cycleId">The cycle whose plan was vetoed. A cycle has one plan, so it is the key.</param>
/// <param name="userId">The user whose agent the cycle was of.</param>
/// <param name="modelId">The model of the catalog that was the Risk Manager, and vetoed the plan.</param>
/// <param name="mode">The mode that the agent was in.</param>
/// <param name="market">Its market then.</param>
/// <param name="symbol">The symbol that it traded.</param>
/// <param name="timeframe">Its timeframe then.</param>
/// <param name="closedAt">The close of the candle that the cycle was for.</param>
/// <param name="side">The side of the position that the plan would have opened.</param>
/// <param name="entryPrice">The price that it is entered at: that of the plan, or the close of the candle for an entry at the market.</param>
/// <param name="resting">Whether the entry is one at a price, which rests until the market reaches it.</param>
/// <param name="stopLoss">The stop-loss of the plan.</param>
/// <param name="takeProfit">The take-profit of the plan.</param>
/// <param name="dueAt">When it is next looked at: the close of the next candle that it has not been followed over.</param>
/// <param name="outcome">How it ended, or null while it has not.</param>
/// <param name="resultInR">What it would have produced, as a multiple of what it risked, or null where it has no result.</param>
/// <param name="exitedAt">The close of the candle that ended it, or null where no candle did.</param>
/// <param name="evaluatedAt">When it was given its outcome.</param>
public sealed class ShadowTrade(
    Guid cycleId,
    Guid userId,
    Guid modelId,
    TradingMode mode,
    Market market,
    string symbol,
    Timeframe timeframe,
    DateTimeOffset closedAt,
    ShadowSide side,
    decimal entryPrice,
    bool resting,
    decimal stopLoss,
    decimal takeProfit,
    DateTimeOffset dueAt,
    ShadowOutcome? outcome = null,
    decimal? resultInR = null,
    DateTimeOffset? exitedAt = null,
    DateTimeOffset? evaluatedAt = null) : IUserOwned
{
    public Guid CycleId { get; } = cycleId;

    public Guid UserId { get; } = userId;

    public Guid ModelId { get; } = modelId;

    public TradingMode Mode { get; } = mode;

    public Market Market { get; } = market;

    public string Symbol { get; } = symbol;

    public Timeframe Timeframe { get; } = timeframe;

    public DateTimeOffset ClosedAt { get; } = closedAt;

    public ShadowSide Side { get; } = side;

    public decimal EntryPrice { get; } = entryPrice;

    public bool Resting { get; } = resting;

    public decimal StopLoss { get; } = stopLoss;

    public decimal TakeProfit { get; } = takeProfit;

    public DateTimeOffset DueAt { get; } = dueAt;

    public ShadowOutcome? Outcome { get; } = outcome;

    public decimal? ResultInR { get; } = resultInR;

    public DateTimeOffset? ExitedAt { get; } = exitedAt;

    public DateTimeOffset? EvaluatedAt { get; } = evaluatedAt;

    /// <summary>
    /// The shadow trade of a vetoed plan as it is written with its cycle: with the prices of
    /// the plan, and with no outcome yet.
    /// </summary>
    /// <remarks>
    /// <b>A plan whose stop-loss or take-profit is on the wrong side of its entry has ended
    /// already, with no result.</b> Nothing asked where they stand before the veto, because the
    /// Risk Guard checks what goes on, and a vetoed plan does not. It would have rejected such a
    /// plan (FR-RSK-4), so no trade would have been made of it.
    /// </remarks>
    /// <param name="cycleId">The cycle.</param>
    /// <param name="userId">The user whose agent the cycle was of.</param>
    /// <param name="modelId">The model that vetoed the plan.</param>
    /// <param name="mode">The mode of the agent.</param>
    /// <param name="market">Its market.</param>
    /// <param name="symbol">Its symbol.</param>
    /// <param name="timeframe">Its timeframe.</param>
    /// <param name="closedAt">The close of the candle that the cycle was for.</param>
    /// <param name="side">The side of the position that the plan would have opened.</param>
    /// <param name="priceOfEntry">The price of an entry at a price, or null for an entry at the market.</param>
    /// <param name="priceAtClose">The close of the candle that the cycle was for, which an entry at the market is entered at.</param>
    /// <param name="stopLoss">The stop-loss of the plan.</param>
    /// <param name="takeProfit">The take-profit of the plan.</param>
    /// <param name="recordedAt">When the cycle ended, which is when a trade that has ended already is said to have.</param>
    public static ShadowTrade Of(
        Guid cycleId,
        Guid userId,
        Guid modelId,
        TradingMode mode,
        Market market,
        string symbol,
        Timeframe timeframe,
        DateTimeOffset closedAt,
        ShadowSide side,
        decimal? priceOfEntry,
        decimal priceAtClose,
        decimal stopLoss,
        decimal takeProfit,
        DateTimeOffset recordedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var at = closedAt.ToUniversalTime();
        var entryPrice = priceOfEntry ?? priceAtClose;
        var sound = ShadowTradeRule.IsSound(side, entryPrice, stopLoss, takeProfit);

        return new ShadowTrade(
            cycleId,
            userId,
            modelId,
            mode,
            market,
            symbol,
            timeframe,
            at,
            side,
            entryPrice,
            resting: priceOfEntry is not null,
            stopLoss,
            takeProfit,

            // The first candle after it is the first that says anything of it.
            dueAt: at + timeframe.Length(),
            outcome: sound ? null : ShadowOutcome.Rejected,
            evaluatedAt: sound ? null : recordedAt.ToUniversalTime());
    }
}

internal sealed class ShadowTradeConfiguration : IEntityTypeConfiguration<ShadowTrade>
{
    public void Configure(EntityTypeBuilder<ShadowTrade> builder)
    {
        builder.ToTable("shadow_trades");

        // A cycle has one plan, and so one shadow trade at the most.
        builder.HasKey(trade => trade.CycleId);

        builder.Property(trade => trade.CycleId).ValueGeneratedNever();
        builder.Property(trade => trade.UserId);
        builder.Property(trade => trade.ModelId);
        builder.Property(trade => trade.Mode);
        builder.Property(trade => trade.Market);
        builder.Property(trade => trade.Symbol);
        builder.Property(trade => trade.Timeframe);
        builder.Property(trade => trade.ClosedAt);
        builder.Property(trade => trade.Side);
        builder.Property(trade => trade.EntryPrice);
        builder.Property(trade => trade.Resting);
        builder.Property(trade => trade.StopLoss);
        builder.Property(trade => trade.TakeProfit);
        builder.Property(trade => trade.DueAt);
        builder.Property(trade => trade.Outcome);
        builder.Property(trade => trade.ResultInR);
        builder.Property(trade => trade.ExitedAt);
        builder.Property(trade => trade.EvaluatedAt);

        // The shadow trades of one user, by when their plans were made: what the measure of a
        // Risk Manager is worked out from, for all of them or for a period (FR-EXP-3).
        builder.HasIndex(trade => new { trade.UserId, trade.ClosedAt });

        // The shadow trades that have not ended, which the job asks for with every run: few of
        // all that there ever were.
        builder.HasIndex(trade => trade.DueAt).HasFilter("outcome IS NULL");

        // A model of the catalog is never deleted (FR-CAT-5), so the row finds the model that
        // vetoed the plan, and its provider, for good.
        builder.HasOne<AiModel>()
            .WithMany()
            .HasForeignKey(trade => trade.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        // The key to the cycle, which carries the owner along, is declared by the Decision
        // Engine, in ExperienceShadowTradeConfiguration: Experience may not name a cycle.
    }
}
