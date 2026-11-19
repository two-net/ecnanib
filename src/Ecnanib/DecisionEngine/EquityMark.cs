using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What the loss limits of an agent are counted from, as a row of the <c>equity_marks</c> table:
/// its result as the UTC day began, which its loss of the day is counted from, and the highest
/// that its result has been, which its drawdown is counted from (FR-RSK-4, FR-RSK-8). See "The
/// loss limits" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A mark is a result, and no equity.</b> The equity of an agent is the capital that its user
/// allocated to it, plus its net profit or loss to date. The marks keep the profit or loss
/// alone, with the capital that they were worked out under.
/// </para>
/// <para>
/// <b>A capital that its user changes reaches no loss limit by itself.</b> What the agent has
/// lost of its equity in the day, and from its peak, is the same share after the change as
/// before it: the marks are worked out anew for the capital as it is now, and are stored with
/// it. See <see cref="EquityMarks.At"/>.
/// </para>
/// <para>
/// <b>The marks are of the asset that the symbol of the agent is priced in.</b> The result of an
/// agent that was given a symbol which is priced in another asset starts anew in that asset,
/// and so do its marks.
/// </para>
/// <para>
/// <b>An agent has one row for each mode that it traded in.</b> What it did in Demo is never
/// combined with what it does in Live (FR-BIN-11): its result in Live starts at nothing, and so
/// do its marks.
/// </para>
/// <para>
/// It carries its owner along to the agent that it hangs on, as a position does:
/// <c>(agent_id, user_id)</c> points at <c>agents (id, user_id)</c>. See docs/ownership.md. Only
/// what watches the loss limits writes it: see <see cref="LossLimitWatch"/>.
/// </para>
/// </remarks>
/// <param name="agentId">The agent whose marks they are.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="mode">The mode that the result is of.</param>
/// <param name="asset">The asset that the result is in: the one that the symbol of the agent was priced in when the marks were written.</param>
/// <param name="capital">The capital that the agent was allocated when the marks were written.</param>
/// <param name="dayBeganAt">The start of the UTC day that <paramref name="resultAtDayStart"/> is of: midnight, UTC.</param>
/// <param name="resultAtDayStart">The result of the agent as that day began: as it was when it was first worked out in the day.</param>
/// <param name="peakResult">
/// The highest that the result of the agent has been, since it began or since its drawdown limit
/// last halted it: its peak equity is this above its capital.
/// </param>
/// <param name="updatedAt">When the row was last written.</param>
public sealed class EquityMark(
    Guid agentId,
    Guid userId,
    TradingMode mode,
    string asset,
    decimal capital,
    DateTimeOffset dayBeganAt,
    decimal resultAtDayStart,
    decimal peakResult,
    DateTimeOffset updatedAt) : IUserOwned
{
    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public TradingMode Mode { get; } = mode;

    public string Asset { get; } = asset;

    public decimal Capital { get; } = capital;

    public DateTimeOffset DayBeganAt { get; } = dayBeganAt;

    public decimal ResultAtDayStart { get; } = resultAtDayStart;

    public decimal PeakResult { get; } = peakResult;

    public DateTimeOffset UpdatedAt { get; } = updatedAt;
}

internal sealed class EquityMarkConfiguration : IEntityTypeConfiguration<EquityMark>
{
    public void Configure(EntityTypeBuilder<EquityMark> builder)
    {
        builder.ToTable("equity_marks");

        // One row for an agent in a mode: what it did in Demo and in Live is kept apart (FR-BIN-11).
        builder.HasKey(mark => new { mark.AgentId, mark.Mode });

        builder.Property(mark => mark.AgentId);
        builder.Property(mark => mark.UserId);
        builder.Property(mark => mark.Mode);
        builder.Property(mark => mark.Asset);
        builder.Property(mark => mark.Capital);
        builder.Property(mark => mark.DayBeganAt);
        builder.Property(mark => mark.ResultAtDayStart);
        builder.Property(mark => mark.PeakResult);
        builder.Property(mark => mark.UpdatedAt);

        // The marks of an agent go with it when it is deleted, and with the account when that
        // is. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(mark => new { mark.AgentId, mark.UserId })
            .HasPrincipalKey(agent => new { agent.Id, agent.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// What the marks of an agent are at a moment, from the row that is stored and the result of the
/// agent then: the one place that says when a day is new and when a peak is higher, for what
/// writes the marks and for what only reads them.
/// </summary>
internal static class EquityMarks
{
    /// <summary>The start of the UTC day that a moment is in: midnight, UTC.</summary>
    public static DateTimeOffset DayOf(DateTimeOffset moment) => new(moment.UtcDateTime.Date, TimeSpan.Zero);

    /// <summary>
    /// The marks of an agent as they are at a moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The result as the UTC day began is the first that is worked out in the day.</b> The
    /// close of midnight starts a cycle of every agent that runs, and a position is reconciled
    /// within minutes, so that is the result as the day began. A row of an earlier day gives
    /// the result of now: nothing was lost in a day that has just begun.
    /// </para>
    /// <para>
    /// <b>The peak is the highest result that was seen.</b> An agent that has no row yet began
    /// with its capital, which is a result of nothing: its peak is that, or its result now where
    /// that is higher. A row that is stored is not held to that: the peak of an agent whose
    /// drawdown limit halted it starts anew at the result that it had then, which is a loss.
    /// </para>
    /// <para>
    /// <b>A row of another asset is no row.</b> The agent was given a symbol that is priced in
    /// another asset since: its result there starts anew, and what was kept in the other asset
    /// says nothing of it.
    /// </para>
    /// <para>
    /// <b>A row of another capital is worked out anew for the capital of now.</b> The equity of
    /// the agent is the same share of its equity as the day began, and of its peak equity,
    /// after its user changed the capital as it was before: see <see cref="Under"/>.
    /// </para>
    /// </remarks>
    /// <param name="stored">The row of the agent in its mode, or null when it has none yet.</param>
    /// <param name="result">The result of the agent now, with unrealized profit and loss.</param>
    /// <param name="capital">The capital that the agent is allocated now.</param>
    /// <param name="asset">The asset that the symbol of the agent is priced in now.</param>
    /// <param name="now">The moment.</param>
    /// <returns>The start of the UTC day that the marks are of, the result as that day began, and the peak.</returns>
    public static (DateTimeOffset DayBeganAt, decimal AtDayStart, decimal Peak) At(
        EquityMark? stored, decimal result, decimal capital, string asset, DateTimeOffset now)
    {
        var today = DayOf(now);

        if (stored is null || stored.Asset != asset)
        {
            return (today, result, Math.Max(result, 0m));
        }

        var peak = Math.Max(Under(capital, stored.PeakResult, stored.Capital, result), result);

        // A row of a later day than the clock shows stays as it is: a clock that was set back
        // begins no day anew.
        return stored.DayBeganAt < today
            ? (today, result, peak)
            : (stored.DayBeganAt, Under(capital, stored.ResultAtDayStart, stored.Capital, result), peak);
    }

    /// <summary>
    /// A mark that was worked out under one capital, as it is under another: the result at
    /// which the equity of the agent now is the same share of the equity of the mark as it was
    /// under the capital before. So a capital that is lowered makes no loss a larger share of
    /// the equity, and one that is raised makes none a smaller share: the change reaches no
    /// loss limit, and takes an agent out of none.
    /// </summary>
    /// <remarks>
    /// Where a share cannot be taken, because an equity is not above zero under either capital,
    /// the mark stays the amount that it is.
    /// </remarks>
    /// <param name="capital">The capital now.</param>
    /// <param name="mark">The mark, as a result under the capital before.</param>
    /// <param name="before">The capital that the mark was worked out under.</param>
    /// <param name="result">The result of the agent now.</param>
    public static decimal Under(decimal capital, decimal mark, decimal before, decimal result)
    {
        if (capital == before)
        {
            return mark;
        }

        var equityBefore = before + result;
        var markedBefore = before + mark;
        var equity = capital + result;

        if (equityBefore <= 0m || markedBefore <= 0m || equity <= 0m)
        {
            return mark;
        }

        try
        {
            return Math.Round((equity * markedBefore / equityBefore) - capital, AgentResults.Places);
        }
        catch (OverflowException)
        {
            // Numbers that cannot be worked out with are no numbers of a market: the mark stays.
            return mark;
        }
    }
}
