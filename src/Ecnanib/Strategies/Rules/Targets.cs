using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;

namespace Ecnanib.Strategies.Rules;

/// <summary>
/// Targets in R: a partial at <c>partialTakeProfitR</c>, then the nearest prior swing when it offers at least
/// <c>minRewardR</c> ("≥ 2R at the prior swing"), else the fallback multiple (<c>typicalRewardR</c> or
/// <c>minRewardR</c>); the 1D strategies carry only the partial and trail.
/// </summary>
internal static class Targets
{
    public static IReadOnlyList<decimal> Compute(Frame f, Direction d, decimal entry, decimal r, bool hasFinal, decimal? fallbackR, out decimal rewardR, out string? finalSource)
    {
        finalSource = null;
        var partial = d.Offset(entry, f.Params.Dec(SpecificParameters.PartialTakeProfitR) * r);
        decimal? final = null;
        if (hasFinal)
        {
            var minRewardR = f.Params.Dec(SpecificParameters.MinRewardR);
            if (PriorSwing(f.SwingPoints, entry, d) is { } swing && d.Sign * (swing - entry) / r >= minRewardR)
            {
                final = swing;
                finalSource = "prior swing";
            }
            else if (fallbackR is { } multiple)
            {
                final = d.Offset(entry, multiple * r);
                finalSource = $"{Prices.Format(multiple)}R";
            }
        }

        var targets = new List<decimal>();
        if (final is { } price && d.Beyond(price, partial))
        {
            targets.Add(partial);
            targets.Add(price);
        }
        else
        {
            targets.Add(final ?? partial);
        }

        rewardR = d.Sign * (targets[^1] - entry) / r;
        return targets;
    }

    /// <summary>The nearest confirmed swing high above the entry (long) or swing low below it (short).</summary>
    public static decimal? PriorSwing(IReadOnlyList<SwingPoint> swings, decimal entry, Direction d)
    {
        var kind = d.IsLong ? SwingKind.High : SwingKind.Low;
        decimal? nearest = null;
        foreach (var point in swings)
        {
            if (point.Kind != kind || !d.Beyond(point.Price, entry))
                continue;
            if (nearest is null || d.Beyond(nearest.Value, point.Price))
                nearest = point.Price;
        }

        return nearest;
    }
}
