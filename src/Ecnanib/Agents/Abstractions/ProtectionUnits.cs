using System.Text.Json;

namespace Ecnanib.Agents.Abstractions;

/// <summary>A protection unit of a position: a quantity and, when it has one, the target that takes it off (Appendix A, FR-EXE-06).</summary>
public sealed record ProtectionUnit(decimal Quantity, decimal? Target);

/// <summary>
/// Splits a position into its protection units (FR-EXE-01/02/06): the partial unit, <paramref name="partialFraction"/>
/// of the size rounded down to the lot step, takes the first target; the remainder takes the second target or trails
/// without one. A unit that would be below the step or the minimum quantity is merged into the other, so a position too
/// small to split is protected as one unit.
/// </summary>
public static class ProtectionUnits
{
    public static IReadOnlyList<ProtectionUnit> Split(decimal quantity, IReadOnlyList<decimal> targets, decimal partialFraction, decimal stepSize = 0m, decimal minQuantity = 0m)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (quantity <= 0)
            return [];
        if (targets.Count == 0)
            return [new ProtectionUnit(quantity, null)];

        decimal? final = targets.Count > 1 ? targets[1] : null;
        var partial = Floor(quantity * Math.Clamp(partialFraction, 0m, 1m), stepSize);
        if (partial <= 0 || partial < minQuantity)
            return [new ProtectionUnit(quantity, final ?? targets[0])];
        var rest = Normalize(quantity - partial);
        if (rest <= 0 || rest < minQuantity)
            return [new ProtectionUnit(quantity, targets[0])];
        return [new ProtectionUnit(partial, targets[0]), new ProtectionUnit(rest, final)];
    }

    /// <summary>The strategy's <c>partialTakeProfitFraction</c> from its resolved parameters, clamped to [0, 1]; one half when it has none.</summary>
    public static decimal PartialFraction(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.TryGetValue(StrategyParameters.PartialTakeProfitFraction, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var fraction)
            ? Math.Clamp(fraction, 0m, 1m)
            : 0.5m;
    }

    /// <summary>Rounds down to the step grid, or to 8 decimals when the venue has no grid (paper).</summary>
    public static decimal Floor(decimal value, decimal stepSize) =>
        stepSize <= 0 ? Normalize(Math.Round(value, 8, MidpointRounding.ToZero)) : Normalize(decimal.Floor(value / stepSize) * stepSize);

    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}
