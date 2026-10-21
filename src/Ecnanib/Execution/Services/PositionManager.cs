using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Execution.Services;

/// <summary>A stop to move to, with the Appendix A rule that produced it (<c>breakeven</c> or <c>trail</c>).</summary>
internal sealed record StopChange(decimal Stop, string Reason, string Detail);

/// <summary>
/// The strategy rules applied to an open position at candle close (FR-EXE-06, Appendix A): once the partial has
/// filled, the stop moves to breakeven and then trails behind the engine's <c>trailLong</c>/<c>trailShort</c> level
/// (swing or EMA per <c>trailingMethod</c>) less the stop buffer in ATR. A stop only ever tightens (FR-AI-15) and never
/// sits on the wrong side of the last close. Pure: the venue applies the change.
/// </summary>
internal static class PositionManager
{
    public const string Breakeven = "breakeven";

    public const string Trail = "trail";

    public static StopChange? Advise(Trade trade, StrategyOutput output, IReadOnlyDictionary<string, JsonElement> parameters, SymbolFilters? filters, decimal lastClose)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(parameters);
        if (trade.CurrentStop is not { } current || trade.IsUnmanaged || lastClose <= 0)
            return null;
        var remaining = trade.RemainingSize > 0 ? trade.RemainingSize : trade.Size;
        if (remaining >= trade.Size)
            return null;

        var isLong = trade.Side == PositionSide.Long;
        var candidates = new List<(decimal Price, string Reason, string Detail)> { (trade.EntryPrice, Breakeven, $"breakeven at the entry {Sizer.N(trade.EntryPrice)}") };
        var level = output.Levels.FirstOrDefault(l => l.Name == (isLong ? "trailLong" : "trailShort"));
        if (level is not null && output.Indicators.TryGetValue("atr", out var atr) && atr > 0)
        {
            var buffer = ReadDecimal(parameters, StrategyParameters.StopBufferAtr) * atr;
            var trail = isLong ? level.Price - buffer : level.Price + buffer;
            candidates.Add((trail, Trail, $"trailing {Sizer.N(level.Price)} with a buffer of {Sizer.N(buffer)}"));
        }

        var acceptable = candidates
            .Select(c => (Price: Round(c.Price, isLong, filters), c.Reason, c.Detail))
            .Where(c => c.Price > 0 && (isLong ? c.Price > current && c.Price < lastClose : c.Price < current && c.Price > lastClose))
            .ToList();
        if (acceptable.Count == 0)
            return null;
        var best = isLong ? acceptable.MaxBy(c => c.Price) : acceptable.MinBy(c => c.Price);
        return new StopChange(best.Price, best.Reason, $"{best.Detail}: stop {Sizer.N(current)} → {Sizer.N(best.Price)}");
    }

    /// <summary>A stop is rounded away from the price so the grid never loosens it: down for a long, up for a short.</summary>
    public static decimal Round(decimal stop, bool isLong, SymbolFilters? filters) =>
        filters is null ? stop : isLong ? filters.RoundPriceDownToTick(stop) : filters.RoundPriceUpToTick(stop);

    /// <summary>The strategy's entry timeout in candles, a common parameter of every Appendix A strategy.</summary>
    public static int EntryTimeoutCandles(IReadOnlyDictionary<string, JsonElement> parameters, int fallback) =>
        parameters.TryGetValue(StrategyParameters.EntryTimeoutCandles, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var candles) && candles > 0
            ? candles
            : fallback;

    public static bool MarketFallback(IReadOnlyDictionary<string, JsonElement> parameters) =>
        parameters.TryGetValue(StrategyParameters.MarketFallback, out var value) && value.ValueKind == JsonValueKind.True;

    public static decimal ReadDecimal(IReadOnlyDictionary<string, JsonElement> parameters, string name, decimal fallback = 0m) =>
        parameters.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : fallback;
}
