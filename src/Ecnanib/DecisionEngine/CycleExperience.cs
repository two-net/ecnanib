using System.Text.Json;
using Ecnanib.Experience;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What Experience is to judge of a cycle, once the candles after it have closed: the call of
/// each analyst that called a direction (FR-EXP-3), and the plan that the Risk Manager vetoed,
/// as a shadow trade (FR-EXP-5). It is stored with the cycle. See "What a cycle leaves for
/// Experience" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is made from the record of the cycle, and from nothing else:</b> the row of the cycle,
/// with the summary of its data package, and the calls of its roles, as they are stored. So what
/// Experience judges is what the record says, and what stores a cycle is handed nothing more
/// for it.
/// </para>
/// <para>
/// <b>Only a cycle that a candle close started leaves anything.</b> A test cycle places no
/// order, and its user runs as many of them for one candle as they like: it would be the same
/// call, counted again (FR-CYC-12).
/// </para>
/// <para>
/// <b>It reads the answer of a call that is valid, and the latest of them.</b> An answer that
/// was rejected is no call. The veto is the outcome of the cycle, and not the answer of the
/// Risk Manager, which is not always kept: the row of its call says the model.
/// </para>
/// <para>
/// <b>It fails nothing.</b> What cannot be read leaves no row, and the cycle is stored all the
/// same: the record of a cycle is not given up for what is worked out from it.
/// </para>
/// </remarks>
internal static class CycleExperience
{
    private static readonly Role[] Analysts = [Role.MarketAnalyst, Role.SentimentAnalyst];

    /// <summary>The rows of Experience that a cycle is stored with: its calls to score, and its shadow trade. Empty where it leaves none.</summary>
    /// <param name="cycle">The cycle, as it is stored.</param>
    /// <param name="outputs">The calls of its roles, as they are stored.</param>
    public static IReadOnlyList<object> Of(DecisionCycle cycle, IReadOnlyList<RoleOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(outputs);

        // The candle that the team decided on is the one that the cycle was for: its close is
        // the price that a call is scored from, and that an entry at the market is entered at.
        if (cycle.Kind != CycleKind.Scheduled
            || DataPackageSummary.Read(cycle.DataPackage) is not { Series: [{ Latest: { } candle }, ..] } package
            || candle.ClosedAt != cycle.ClosedAt
            || candle.Close <= 0m
            || string.IsNullOrWhiteSpace(package.Symbol))
        {
            return [];
        }

        var rows = new List<object>();

        foreach (var role in Analysts)
        {
            if (Read(() => CallOf(cycle, package, candle.Close, ValidOf(outputs, role))) is { } call)
            {
                rows.Add(call);
            }
        }

        if (cycle.Outcome == CycleOutcome.Vetoed && Read(() => ShadowOf(cycle, package, candle.Close, outputs)) is { } trade)
        {
            rows.Add(trade);
        }

        return rows;
    }

    /// <summary>The call of an analyst, or null where it gave no valid answer, or called no direction.</summary>
    private static AnalystCall? CallOf(DecisionCycle cycle, DataPackageSummary package, decimal close, RoleOutput? answered)
    {
        if (answered?.Result is not { } result)
        {
            return null;
        }

        using var answer = JsonDocument.Parse(result);

        CallDirection? direction = Named<Bias>(answer.RootElement.GetProperty("bias")) switch
        {
            Bias.Bullish => CallDirection.Bullish,
            Bias.Bearish => CallDirection.Bearish,
            _ => null,
        };

        return AnalystCall.Of(
            cycle.Id,
            cycle.UserId,
            answered.Role,
            answered.ModelId,
            cycle.Mode,
            package.Market,
            package.Symbol,
            package.Timeframe,
            cycle.ClosedAt,
            direction,
            close);
    }

    /// <summary>The shadow trade of the plan that was vetoed, or null where the record does not say the plan or who reviewed it.</summary>
    private static ShadowTrade? ShadowOf(DecisionCycle cycle, DataPackageSummary package, decimal close, IReadOnlyList<RoleOutput> outputs)
    {
        if (ValidOf(outputs, Role.Strategist)?.Result is not { } planned || ValidOf(outputs, Role.RiskManager) is not { } reviewed)
        {
            return null;
        }

        using var answer = JsonDocument.Parse(planned);
        var plan = answer.RootElement;

        // A plan that reverses names no side: it enters on the other side of what the agent held.
        ShadowSide? side = Named<PlanAction>(plan.GetProperty("action")) switch
        {
            PlanAction.OpenLong => ShadowSide.Long,
            PlanAction.OpenShort => ShadowSide.Short,
            PlanAction.Reverse when package.Position is { Side: PositionSide.Long } => ShadowSide.Short,
            PlanAction.Reverse when package.Position is { Side: PositionSide.Short } => ShadowSide.Long,
            _ => null,
        };

        if (side is not { } opened || PriceOf(plan, "stopLoss") is not { } stopLoss || PriceOf(plan, "takeProfit") is not { } takeProfit)
        {
            return null;
        }

        // An entry at a price names its price, and an entry at the market has none.
        decimal? priceOfEntry;

        switch (Named<EntryType>(plan.GetProperty("entryType")))
        {
            case EntryType.Market:
                priceOfEntry = null;
                break;

            case EntryType.Limit when PriceOf(plan, "entryPrice") is { } named:
                priceOfEntry = named;
                break;

            default:
                return null;
        }

        return ShadowTrade.Of(
            cycle.Id,
            cycle.UserId,
            reviewed.ModelId,
            cycle.Mode,
            package.Market,
            package.Symbol,
            package.Timeframe,
            cycle.ClosedAt,
            opened,
            priceOfEntry,
            close,
            stopLoss,
            takeProfit,
            cycle.EndedAt);
    }

    /// <summary>The valid call of a role, and the latest of them where a cycle has more than one.</summary>
    private static RoleOutput? ValidOf(IReadOnlyList<RoleOutput> outputs, Role role) =>
        outputs
            .Where(output => output.Role == role && output.Validation == RoleValidation.Valid)
            .OrderByDescending(output => output.Attempt)
            .FirstOrDefault();

    /// <summary>A price of a stored plan: a number above nothing, or null.</summary>
    private static decimal? PriceOf(JsonElement plan, string field) =>
        plan.GetProperty(field) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var price) && price > 0m ? price : null;

    /// <summary>The member that a text of a schema names: <c>open_long</c> is <c>OpenLong</c>. Null for a text that names none, and for no text.</summary>
    private static TEnum? Named<TEnum>(JsonElement value)
        where TEnum : struct, Enum =>
        value.ValueKind == JsonValueKind.String
        && Enum.TryParse<TEnum>(value.GetString()!.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var named)
        && Enum.IsDefined(named)
            ? named
            : null;

    /// <summary>What a stored answer says, or null where it does not read as what its schema says.</summary>
    private static T? Read<T>(Func<T?> row)
        where T : class
    {
        try
        {
            return row();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return null;
        }
    }
}
