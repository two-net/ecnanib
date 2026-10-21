using System.Globalization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Execution.Services;

/// <summary>What the sizing arithmetic needs (FR-EXE-04). Prices are raw; the sizer rounds them to the tick.</summary>
internal sealed record SizingInput(
    PositionSide Side,
    decimal Price,
    decimal Stop,
    decimal RiskPercent,
    decimal Equity,
    decimal Available,
    decimal SizeMultiplier,
    SymbolFilters Filters,
    decimal? MaxNotional,
    int Leverage,
    bool MarketOrder,
    MarketType Market);

/// <summary><see cref="Quantity"/> is 0 when <see cref="Rejected"/>; <see cref="Price"/> and <see cref="Stop"/> are on the tick grid.</summary>
internal sealed record SizingResult(decimal Quantity, decimal Price, decimal Stop, IReadOnlyList<LimitCheck> Checks)
{
    public bool Rejected => Checks.Any(c => c.Status == LimitCheckStatus.Fail);

    public IEnumerable<string> FailedLimits => Checks.Where(c => c.Status == LimitCheckStatus.Fail).Select(c => c.Limit);
}

/// <summary>
/// The position size of FR-EXE-04: (equity × risk per trade) ÷ stop distance, times the strategy's size multiplier,
/// rounded down to the symbol's lot step, then clamped to the agent's notional cap and the exchange's maximum quantity
/// and refused under the minimum quantity, the minimum notional or the free balance. Pure, so the arithmetic is
/// testable without a venue; every step is a <see cref="LimitCheck"/> for <c>decisions.execution_check</c>.
/// </summary>
internal static class Sizer
{
    public static SizingResult Size(SizingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var checks = new List<LimitCheck>();
        var filters = input.Filters;
        var isLong = input.Side == PositionSide.Long;
        var price = filters.RoundPriceToTick(input.Price);
        var stop = isLong ? filters.RoundPriceDownToTick(input.Stop) : filters.RoundPriceUpToTick(input.Stop);
        var distance = Math.Abs(price - stop);
        if (price <= 0 || distance <= 0 || (isLong ? stop >= price : stop <= price))
        {
            checks.Add(new LimitCheck(ExecutionLimitNames.Size, LimitCheckStatus.Fail, $"stop {N(stop)} is not on the losing side of the entry {N(price)} after tick rounding"));
            return new SizingResult(0m, price, stop, checks);
        }

        var riskAmount = input.Equity * input.RiskPercent / 100m;
        var raw = riskAmount / distance * input.SizeMultiplier;
        var quantity = filters.RoundQuantityDownToStep(raw, input.MarketOrder);
        var formula = $"{N(input.Equity)} × {N(input.RiskPercent)}% ÷ {N(distance)}" + (input.SizeMultiplier == 1m ? "" : $" × {N(input.SizeMultiplier)}") + $" = {N(raw)}, {N(quantity)} on the lot grid";
        checks.Add(new LimitCheck(ExecutionLimitNames.Size, LimitCheckStatus.Pass, formula));

        var maxNotional = input.MaxNotional;
        if (filters.MaxNotional is > 0 && (maxNotional is null || filters.MaxNotional < maxNotional))
            maxNotional = filters.MaxNotional;
        if (maxNotional is { } cap && quantity * price > cap)
        {
            var capped = filters.RoundQuantityDownToStep(cap / price, input.MarketOrder);
            checks.Add(new LimitCheck(ExecutionLimitNames.Notional, LimitCheckStatus.Clamped, $"{N(quantity * price)} reduced to the cap of {N(cap)}: {N(capped)}"));
            quantity = capped;
        }
        else
        {
            checks.Add(new LimitCheck(ExecutionLimitNames.Notional, LimitCheckStatus.Pass, maxNotional is { } c ? $"{N(quantity * price)} within the cap of {N(c)}" : "no notional cap"));
        }

        var maxQuantity = input.MarketOrder && filters.MarketMaxQuantity is > 0 ? Math.Min(filters.MaxQuantity > 0 ? filters.MaxQuantity : decimal.MaxValue, filters.MarketMaxQuantity.Value) : filters.MaxQuantity;
        if (maxQuantity > 0 && quantity > maxQuantity)
        {
            var capped = filters.RoundQuantityDownToStep(maxQuantity, input.MarketOrder);
            checks.Add(new LimitCheck(ExecutionLimitNames.MaxQuantity, LimitCheckStatus.Clamped, $"{N(quantity)} reduced to the exchange maximum of {N(capped)}"));
            quantity = capped;
        }

        var minQuantity = Math.Max(filters.MinQuantity, input.MarketOrder ? filters.MarketMinQuantity ?? 0m : 0m);
        if (quantity <= 0 || quantity < minQuantity)
        {
            checks.Add(new LimitCheck(ExecutionLimitNames.MinQuantity, LimitCheckStatus.Fail, $"{N(quantity)} is below the minimum of {N(minQuantity)}"));
            return new SizingResult(0m, price, stop, checks);
        }

        checks.Add(new LimitCheck(ExecutionLimitNames.MinQuantity, LimitCheckStatus.Pass, $"{N(quantity)} at least {N(minQuantity)}"));

        var notional = quantity * price;
        if (!filters.MeetsMinNotional(price, quantity, input.MarketOrder))
        {
            checks.Add(new LimitCheck(ExecutionLimitNames.MinNotional, LimitCheckStatus.Fail, $"{N(notional)} is below the minimum notional of {N(filters.MinNotional)}"));
            return new SizingResult(0m, price, stop, checks);
        }

        checks.Add(new LimitCheck(ExecutionLimitNames.MinNotional, LimitCheckStatus.Pass, $"{N(notional)} at least {N(filters.MinNotional)}"));

        var needed = input.Market == MarketType.Spot ? notional : notional / Math.Max(1, input.Leverage);
        if (needed > input.Available)
        {
            checks.Add(new LimitCheck(ExecutionLimitNames.Balance, LimitCheckStatus.Fail, $"{N(needed)} needed, {N(input.Available)} available"));
            return new SizingResult(0m, price, stop, checks);
        }

        checks.Add(new LimitCheck(ExecutionLimitNames.Balance, LimitCheckStatus.Pass, $"{N(needed)} needed, {N(input.Available)} available"));
        return new SizingResult(quantity, price, stop, checks);
    }

    /// <summary>Plain decimal without trailing zeros, as the detail text reads best.</summary>
    public static string N(decimal value) => SymbolFilters.Normalize(value).ToString("0.############################", CultureInfo.InvariantCulture);
}
