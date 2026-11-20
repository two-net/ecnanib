using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Experience;

/// <summary>
/// A call of an analyst that is scored against the candles after it, as a row of the
/// <c>analyst_calls</c> table: the direction that the Market Analyst or the Sentiment Analyst
/// called in a cycle, and whether the price changed that way over the next 4 closed candles
/// (FR-EXP-3). See "The calls of the analysts" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is written with its cycle, and completed once.</b> What records a cycle writes the row
/// with what the call said and the price that it was made on, and with no outcome. A job gives
/// it its outcome once the candles after it have closed, and nothing changes it after that.
/// </para>
/// <para>
/// <b>It says what its cycle says, in columns of its own.</b> The mode, the market, the symbol,
/// and the timeframe are those of the agent when the cycle ran, which the cycle keeps in the
/// summary of its data package alone. So the measure of a model is worked out from these rows,
/// and no summary is read for it. What an agent did in Demo stays Demo (FR-BIN-11).
/// </para>
/// <para>
/// <b>A neutral call has no row:</b> it called no direction, and is no sample.
/// </para>
/// <para>
/// It carries its owner along to the cycle that it hangs on: <c>(cycle_id, user_id)</c> points
/// at <c>decision_cycles (id, user_id)</c>, which the Decision Engine declares, because
/// Experience may not use it. So the row goes with its cycle, with the agent of that cycle, and
/// with the account. See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="cycleId">The cycle that the call was made in.</param>
/// <param name="role">The analyst: the Market Analyst or the Sentiment Analyst.</param>
/// <param name="userId">The user whose agent the cycle was of.</param>
/// <param name="modelId">The model of the catalog that made the call.</param>
/// <param name="mode">The mode that the agent was in.</param>
/// <param name="market">Its market then.</param>
/// <param name="symbol">The symbol that it traded.</param>
/// <param name="timeframe">Its timeframe then.</param>
/// <param name="closedAt">The close of the candle that the cycle was for.</param>
/// <param name="direction">The direction that was called.</param>
/// <param name="price">The close of that candle, which the price change is taken from.</param>
/// <param name="dueAt">When the 4th candle after it closes, which is from when the call can be scored.</param>
/// <param name="outcome">What became of the call, or null while it is not scored.</param>
/// <param name="laterPrice">The close of the 4th candle after it, or null where there is none.</param>
/// <param name="scoredAt">When it was given its outcome.</param>
public sealed class AnalystCall(
    Guid cycleId,
    Role role,
    Guid userId,
    Guid modelId,
    TradingMode mode,
    Market market,
    string symbol,
    Timeframe timeframe,
    DateTimeOffset closedAt,
    CallDirection direction,
    decimal price,
    DateTimeOffset dueAt,
    CallOutcome? outcome = null,
    decimal? laterPrice = null,
    DateTimeOffset? scoredAt = null) : IUserOwned
{
    public Guid CycleId { get; } = cycleId;

    public Role Role { get; } = role;

    public Guid UserId { get; } = userId;

    public Guid ModelId { get; } = modelId;

    public TradingMode Mode { get; } = mode;

    public Market Market { get; } = market;

    public string Symbol { get; } = symbol;

    public Timeframe Timeframe { get; } = timeframe;

    public DateTimeOffset ClosedAt { get; } = closedAt;

    public CallDirection Direction { get; } = direction;

    public decimal Price { get; } = price;

    public DateTimeOffset DueAt { get; } = dueAt;

    public CallOutcome? Outcome { get; } = outcome;

    public decimal? LaterPrice { get; } = laterPrice;

    public DateTimeOffset? ScoredAt { get; } = scoredAt;

    /// <summary>
    /// The call of an analyst as it is written with its cycle, with no outcome yet, or null for
    /// one that called no direction: a neutral call is no sample (FR-EXP-3).
    /// </summary>
    /// <param name="cycleId">The cycle.</param>
    /// <param name="userId">The user whose agent the cycle was of.</param>
    /// <param name="role">The analyst.</param>
    /// <param name="modelId">The model that made the call.</param>
    /// <param name="mode">The mode of the agent.</param>
    /// <param name="market">Its market.</param>
    /// <param name="symbol">Its symbol.</param>
    /// <param name="timeframe">Its timeframe.</param>
    /// <param name="closedAt">The close of the candle that the cycle was for.</param>
    /// <param name="direction">The direction that was called, or null for a neutral call.</param>
    /// <param name="price">The close of that candle.</param>
    public static AnalystCall? Of(
        Guid cycleId,
        Guid userId,
        Role role,
        Guid modelId,
        TradingMode mode,
        Market market,
        string symbol,
        Timeframe timeframe,
        DateTimeOffset closedAt,
        CallDirection? direction,
        decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (role is not (Role.MarketAnalyst or Role.SentimentAnalyst))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Only an analyst calls a direction.");
        }

        if (direction is not { } called)
        {
            return null;
        }

        var at = closedAt.ToUniversalTime();

        return new AnalystCall(
            cycleId, role, userId, modelId, mode, market, symbol, timeframe, at, called, price, at + (CallScore.CandlesLater * timeframe.Length()));
    }
}

internal sealed class AnalystCallConfiguration : IEntityTypeConfiguration<AnalystCall>
{
    public void Configure(EntityTypeBuilder<AnalystCall> builder)
    {
        builder.ToTable("analyst_calls");

        // A cycle has one call of each analyst that counts: the valid one.
        builder.HasKey(call => new { call.CycleId, call.Role });

        builder.Property(call => call.CycleId);
        builder.Property(call => call.Role);
        builder.Property(call => call.UserId);
        builder.Property(call => call.ModelId);
        builder.Property(call => call.Mode);
        builder.Property(call => call.Market);
        builder.Property(call => call.Symbol);
        builder.Property(call => call.Timeframe);
        builder.Property(call => call.ClosedAt);
        builder.Property(call => call.Direction);
        builder.Property(call => call.Price);
        builder.Property(call => call.DueAt);
        builder.Property(call => call.Outcome);
        builder.Property(call => call.LaterPrice);
        builder.Property(call => call.ScoredAt);

        // The calls of one user, by when they were made: what the measure of an analyst is
        // worked out from, for all of them or for a period (FR-EXP-3).
        builder.HasIndex(call => new { call.UserId, call.ClosedAt });

        // The calls that are still to be scored, which the job asks for with every run: few of
        // all the calls that there ever were.
        builder.HasIndex(call => call.DueAt).HasFilter("outcome IS NULL");

        // A role is a seeded row, which is never deleted while a row points at it.
        builder.HasOne<RoleRow>()
            .WithMany()
            .HasForeignKey(call => call.Role)
            .OnDelete(DeleteBehavior.Restrict);

        // A model of the catalog is never deleted (FR-CAT-5), so the row finds the model that
        // made the call, and its provider, for good.
        builder.HasOne<AiModel>()
            .WithMany()
            .HasForeignKey(call => call.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        // The key to the cycle, which carries the owner along, is declared by the Decision
        // Engine, in ExperienceAnalystCallConfiguration: Experience may not name a cycle.
    }
}
