using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Experience.Services;

/// <summary>
/// What a shadow outcome re-walks from and how far it got, stored as <c>shadow_outcomes.detail</c> (FR-EXP-03): the
/// strategy's proposed entry, stop and first target, the candle of the decision, the entry timeout and the horizon,
/// then the walk's progress so the page can show it.
/// </summary>
internal sealed class ShadowDetail
{
    public const string Veto = "veto";
    public const string Limits = "limits";
    public const string NoDecision = "no-decision";

    public const string ByStop = "stop";
    public const string ByTarget = "target";
    public const string NotFilled = "not-filled";
    public const string Horizon = "horizon";
    public const string Stale = "stale";
    public const string NoDetail = "no-detail";
    public const string Invalid = "invalid";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Why nothing was opened: <see cref="Veto"/>, <see cref="Limits"/> or <see cref="NoDecision"/>.</summary>
    public string Reason { get; set; } = "";

    public PositionSide Side { get; set; }

    public OrderType EntryType { get; set; }

    public decimal Entry { get; set; }

    public decimal Stop { get; set; }

    public decimal Target { get; set; }

    /// <summary>Open time of the candle whose close produced the decision; the walk starts with the next candle.</summary>
    public DateTimeOffset DecisionCandleOpenTime { get; set; }

    public int EntryTimeoutCandles { get; set; }

    public int HorizonCandles { get; set; }

    public int CandlesSeen { get; set; }

    public DateTimeOffset? LastCandleOpenTime { get; set; }

    /// <summary>Open time of the candle the entry filled in.</summary>
    public DateTimeOffset? FilledAt { get; set; }

    public decimal? FillPrice { get; set; }

    public DateTimeOffset? ResolvedCandleOpenTime { get; set; }

    /// <summary>How it ended: <see cref="ByStop"/>, <see cref="ByTarget"/>, <see cref="NotFilled"/>, <see cref="Horizon"/>, <see cref="Stale"/>, <see cref="NoDetail"/> or <see cref="Invalid"/>.</summary>
    public string? Resolution { get; set; }

    /// <summary>Mark-to-market R at the last candle's close when the horizon passed with the position still open.</summary>
    public decimal? MarkR { get; set; }

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Json);

    /// <summary>The stored detail, or null when there is none or it does not describe a walkable setup.</summary>
    public static ShadowDetail? Parse(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.Object } element)
            return null;
        try
        {
            var detail = element.Deserialize<ShadowDetail>(Json);
            return detail is { Entry: > 0, Stop: > 0, Target: > 0, EntryTimeoutCandles: > 0, HorizonCandles: > 0 } ? detail : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ShadowDetail Copy() => (ShadowDetail)MemberwiseClone();
}

/// <summary>Where a walk got to: still pending, or resolved with the R multiple the setup would have made.</summary>
internal sealed record ShadowStep(ShadowResult Result, decimal? RMultiple, ShadowDetail Detail);

/// <summary>
/// Follows a setup the council did not take through the candles after its decision (FR-EXP-03), the way the paper
/// venue would have traded it: a market entry fills at the next candle's open; a limit entry fills on the first candle
/// within the entry timeout whose range touches the entry price, else the shadow expires unfilled. From the fill candle
/// on, a candle that touches the stop ends it at −1R, else one that touches the target ends it at the setup's reward;
/// a candle that touches both counts as the stop. Once the horizon has passed with neither touched it expires, with
/// the mark-to-market R at the last close kept for display. Deterministic: the same candles give the same step.
/// </summary>
internal static class ShadowWalk
{
    private const int RDecimals = 4;

    public static ShadowStep Walk(ShadowDetail stored, IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(candles);
        var detail = stored.Copy();
        detail.CandlesSeen = 0;
        detail.LastCandleOpenTime = null;
        detail.FilledAt = null;
        detail.FillPrice = null;
        detail.ResolvedCandleOpenTime = null;
        detail.Resolution = null;
        detail.MarkR = null;

        var direction = detail.Side == PositionSide.Long ? 1m : -1m;
        if (detail.Entry <= 0 || (detail.Stop - detail.Entry) * direction >= 0 || (detail.Target - detail.Entry) * direction <= 0)
            return Resolve(detail, ShadowResult.Expired, null, ShadowDetail.Invalid, null);

        var after = candles.Where(c => c.OpenTime > detail.DecisionCandleOpenTime).OrderBy(c => c.OpenTime).ToList();
        detail.CandlesSeen = after.Count;
        detail.LastCandleOpenTime = after.Count == 0 ? null : after[^1].OpenTime;

        var fillIndex = -1;
        for (var i = 0; i < after.Count && i < detail.EntryTimeoutCandles; i++)
        {
            var candle = after[i];
            if (detail.EntryType == OrderType.Market)
            {
                fillIndex = i;
                detail.FillPrice = candle.Open;
                break;
            }

            if (detail.Side == PositionSide.Long ? candle.Low <= detail.Entry : candle.High >= detail.Entry)
            {
                fillIndex = i;
                detail.FillPrice = detail.Entry;
                break;
            }
        }

        if (fillIndex < 0)
        {
            return after.Count >= detail.EntryTimeoutCandles
                ? Resolve(detail, ShadowResult.Expired, null, ShadowDetail.NotFilled, after[Math.Min(after.Count, detail.EntryTimeoutCandles) - 1].OpenTime)
                : new ShadowStep(ShadowResult.Pending, null, detail);
        }

        var fill = detail.FillPrice!.Value;
        detail.FilledAt = after[fillIndex].OpenTime;
        var risk = Math.Abs(fill - detail.Stop);
        if (risk <= 0)
            return Resolve(detail, ShadowResult.Expired, null, ShadowDetail.Invalid, detail.FilledAt);

        for (var i = fillIndex; i < after.Count; i++)
        {
            var candle = after[i];
            var stopHit = detail.Side == PositionSide.Long ? candle.Low <= detail.Stop : candle.High >= detail.Stop;
            if (stopHit)
                return Resolve(detail, ShadowResult.HitStop, -1m, ShadowDetail.ByStop, candle.OpenTime);
            var targetHit = detail.Side == PositionSide.Long ? candle.High >= detail.Target : candle.Low <= detail.Target;
            if (targetHit)
                return Resolve(detail, ShadowResult.HitTarget, R(detail.Target, fill, direction, risk), ShadowDetail.ByTarget, candle.OpenTime);
            if (i - fillIndex + 1 >= detail.HorizonCandles)
            {
                detail.MarkR = R(candle.Close, fill, direction, risk);
                return Resolve(detail, ShadowResult.Expired, detail.MarkR, ShadowDetail.Horizon, candle.OpenTime);
            }
        }

        return new ShadowStep(ShadowResult.Pending, null, detail);
    }

    /// <summary>Ends a pending shadow without candles (no usable detail, or candles that no longer reach back to it).</summary>
    public static ShadowStep Expire(ShadowDetail? stored, string resolution)
    {
        var detail = stored?.Copy() ?? new ShadowDetail { Reason = "" };
        return Resolve(detail, ShadowResult.Expired, null, resolution, null);
    }

    private static ShadowStep Resolve(ShadowDetail detail, ShadowResult result, decimal? r, string resolution, DateTimeOffset? candleOpenTime)
    {
        detail.Resolution = resolution;
        detail.ResolvedCandleOpenTime = candleOpenTime;
        return new ShadowStep(result, r, detail);
    }

    private static decimal R(decimal price, decimal fill, decimal direction, decimal risk) =>
        Math.Round((price - fill) * direction / risk, RDecimals, MidpointRounding.AwayFromZero);
}
