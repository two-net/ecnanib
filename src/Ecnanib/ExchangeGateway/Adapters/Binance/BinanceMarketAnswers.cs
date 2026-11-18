using System.Globalization;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// What Binance's answers to the calls of the market data say, in the words of the gateway
/// (FR-BIN-3). Each reads an answer in whole or not at all: it gives null for an answer that is
/// not what Binance sends to the call, and <see cref="BinanceCall"/> then ends the call as a
/// failure. See docs/exchange.md.
/// </summary>
/// <remarks>
/// Binance writes prices and quantities as text, so that no digit is lost, and they are read as
/// decimals. It writes a time as a count of milliseconds.
/// </remarks>
internal static class BinanceMarketAnswers
{
    // The first and the last moment that a count of milliseconds can name as a time, and still
    // have a millisecond after it.
    private static readonly long EarliestTime = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long LatestTime = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - 1;

    /// <summary>
    /// The candles of an answer, oldest first, the one that is in progress last and among them,
    /// or null when the answer is not a list of candles in the order of their time.
    /// </summary>
    /// <remarks>
    /// A row is a list: when the candle began, its first, highest, lowest, and last price, its
    /// volume, and its last millisecond, and after them what is not read. A candle ends a
    /// millisecond after its last one, which is when the next begins: no length of a candle is
    /// worked out here, so a week is Binance's week.
    /// </remarks>
    public static List<ExchangeCandle>? Candles(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candles = new List<ExchangeCandle>(answer.GetArrayLength());

        foreach (var row in answer.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array
                || row.GetArrayLength() < 7
                || Moment(row[0]) is not { } opened
                || Moment(row[6]) is not { } last
                || last < opened
                || Amount(row[1]) is not { } open
                || Amount(row[2]) is not { } high
                || Amount(row[3]) is not { } low
                || Amount(row[4]) is not { } close
                || Amount(row[5]) is not { } volume
                || (candles.Count > 0 && opened < candles[^1].ClosedAt))
            {
                return null;
            }

            candles.Add(new ExchangeCandle(opened, last.AddMilliseconds(1), open, high, low, close, volume));
        }

        return candles;
    }

    /// <summary>The price of an answer, or null when the answer has none. Nothing is no price.</summary>
    public static decimal? Price(JsonElement answer) =>
        Text(answer, "price") is { } price && Amount(price) is { } amount && amount > 0 ? amount : null;

    /// <summary>
    /// The mark price of an answer, with the funding rate and the time of the next funding, or
    /// null when the answer lacks one of them. A funding rate can be below zero.
    /// </summary>
    public static (decimal MarkPrice, decimal FundingRate, DateTimeOffset NextFundingAt)? MarkPrice(JsonElement answer) =>
        Text(answer, "markPrice") is { } price
            && Amount(price) is { } markPrice
            && markPrice > 0
            && Text(answer, "lastFundingRate") is { } rate
            && Amount(rate, signed: true) is { } fundingRate
            && answer.TryGetProperty("nextFundingTime", out var next)
            && Moment(next) is { } nextFundingAt
                ? (markPrice, fundingRate, nextFundingAt)
                : null;

    /// <summary>The open interest of an answer, or null when the answer has none.</summary>
    public static decimal? OpenInterest(JsonElement answer) =>
        Text(answer, "openInterest") is { } openInterest ? Amount(openInterest) : null;

    /// <summary>
    /// The symbols of an answer that the configuration file lists, in the order of the file,
    /// each with its rules, or null when the answer is not the list of a market's symbols, or
    /// one of those symbols does not read.
    /// </summary>
    /// <param name="answer">The answer, which has every symbol of the market.</param>
    /// <param name="market">
    /// The market. On the Futures market only a perpetual contract is the symbol: Binance lists
    /// the contracts that are delivered there as well.
    /// </param>
    /// <param name="listed">The symbols of the configuration file.</param>
    public static List<ExchangeSymbol>? Symbols(JsonElement answer, Market market, IReadOnlyList<string> listed)
    {
        if (answer.ValueKind != JsonValueKind.Object
            || !answer.TryGetProperty("symbols", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var found = new Dictionary<string, ExchangeSymbol>(StringComparer.Ordinal);

        foreach (var row in rows.EnumerateArray())
        {
            // A row without a name could be any symbol, one of the file included.
            if (Text(row, "symbol") is not { } name || name.GetString() is not { Length: > 0 } symbol)
            {
                return null;
            }

            if (!listed.Contains(symbol, StringComparer.Ordinal)
                || (market == Market.Futures && !Says(row, "contractType", "PERPETUAL")))
            {
                continue;
            }

            // A symbol that is there twice, or whose rules do not read: which rules an order
            // must keep is not known, and a list without it would say that the market lacks it.
            if (Symbol(row, symbol, market) is not { } read || !found.TryAdd(symbol, read))
            {
                return null;
            }
        }

        return [.. listed.Where(found.ContainsKey).Select(symbol => found[symbol])];
    }

    /// <summary>A symbol with its rules, or null when the row lacks one of them.</summary>
    private static ExchangeSymbol? Symbol(JsonElement row, string symbol, Market market)
    {
        if (Text(row, "status") is not { } status
            || Text(row, "baseAsset")?.GetString() is not { Length: > 0 } baseAsset
            || Text(row, "quoteAsset")?.GetString() is not { Length: > 0 } quoteAsset
            || !row.TryGetProperty("filters", out var filters)
            || filters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        decimal? priceStep = null, quantityStep = null, minQuantity = null, maxQuantity = null, minValue = null;

        foreach (var filter in filters.EnumerateArray())
        {
            switch (Text(filter, "filterType")?.GetString())
            {
                case "PRICE_FILTER":
                    priceStep = Rule(filter, "tickSize");
                    break;

                case "LOT_SIZE":
                    quantityStep = Rule(filter, "stepSize");
                    minQuantity = Rule(filter, "minQty");
                    maxQuantity = Rule(filter, "maxQty");
                    break;

                // The least value of an order. The Spot market names the rule one way or the
                // other, and the Futures market names the value differently.
                case "NOTIONAL" or "MIN_NOTIONAL":
                    minValue = Rule(filter, "minNotional") ?? Rule(filter, "notional");
                    break;
            }
        }

        return priceStep is { } price && quantityStep is { } quantity && minQuantity is { } least && maxQuantity is { } most && minValue is { } value
            ? new ExchangeSymbol(
                symbol,
                baseAsset,
                quoteAsset,
                status.ValueEquals("TRADING"),
                price,
                quantity,
                least,
                most,
                value,
                Takes(row, market == Market.Spot ? "STOP_LOSS" : "STOP_MARKET"),
                TakesPairs(row, market),
                Takes(row, market == Market.Spot ? "TAKE_PROFIT" : "TAKE_PROFIT_MARKET"))
            : null;
    }

    /// <summary>
    /// Whether a symbol takes a type of order, which the row says among the types of order that
    /// the symbol takes. A stop that is placed at the market when it is triggered is named
    /// differently on the two markets, and so is a take-profit. A row that names no types takes none.
    /// </summary>
    private static bool Takes(JsonElement row, string orderType)
    {
        if (!row.TryGetProperty("orderTypes", out var types) || types.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var type in types.EnumerateArray())
        {
            if (type.ValueKind == JsonValueKind.String && type.ValueEquals(orderType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a symbol takes the pair of a stop-loss and a take-profit, of which Binance ends
    /// one when the other is triggered: a symbol of the Spot market that says so, and that takes
    /// both orders of the pair. The Futures market has no such pair.
    /// </summary>
    private static bool TakesPairs(JsonElement row, Market market) =>
        market == Market.Spot
        && row.TryGetProperty("ocoAllowed", out var allowed)
        && allowed.ValueKind == JsonValueKind.True
        && Takes(row, "STOP_LOSS")
        && Takes(row, "TAKE_PROFIT");

    /// <summary>An amount of a rule, or null when the rule does not have it.</summary>
    private static decimal? Rule(JsonElement filter, string name) => Text(filter, name) is { } value ? Amount(value) : null;

    /// <summary>Whether a row says a word under a name.</summary>
    private static bool Says(JsonElement row, string name, string word) => Text(row, name) is { } said && said.ValueEquals(word);

    /// <summary>What an answer says under a name, as text, or null when it is not an object or says nothing there as text.</summary>
    private static JsonElement? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value
            : null;

    /// <summary>An amount, which Binance writes as text so that no digit is lost, or null when it is none.</summary>
    private static decimal? Amount(JsonElement value, bool signed = false) =>
        value.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                value.GetString(),
                signed ? NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint : NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount)
                ? amount
                : null;

    /// <summary>The time that a count of milliseconds names, or null when it is none.</summary>
    private static DateTimeOffset? Moment(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var milliseconds)
            && milliseconds >= EarliestTime
            && milliseconds <= LatestTime
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
}
