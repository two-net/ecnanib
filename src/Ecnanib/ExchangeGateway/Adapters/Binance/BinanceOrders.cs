using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// The orders of Binance, behind the common interface: an order at the market and a stop, on the
/// Spot market and on the USDⓈ-margined perpetual Futures market, each on the real exchange and
/// in Binance's test environment (FR-BIN-1, FR-BIN-2, FR-BIN-6). See docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BinanceCall"/> makes every call, once, and the calls it can make are those of
/// <see cref="BinanceEndpoint"/>. What is here is which call an order is, and what each answer
/// says, in the words of the gateway: nothing of Binance's own format leaves this class.
/// </para>
/// <para>
/// <b>A stop is two different things on the two markets.</b> On the Spot market it is an order
/// like any other, of the type <c>STOP_LOSS</c>. On the Futures market Binance takes it as a
/// conditional order, with calls of its own, and gives it an id of its own until it is triggered:
/// then it becomes an order, and is known by that order's id.
/// </para>
/// <para>
/// <b>How a call ended says whether there is an order,</b> and errs on the side of not knowing.
/// A refusal that Binance answers with one of its errors is a refusal: there is no order. No
/// answer, a fault of Binance, an error of which Binance itself says that the execution is
/// unknown, and an answer that does not read leave it unknown. Only Binance's own word that it
/// has no such order is that word.
/// </para>
/// </remarks>
internal sealed partial class BinanceOrders(BinanceCall call) : IExchangeOrders
{
    // The errors of which Binance says that it does not know what became of the call, or that
    // say nothing of it: an unknown error, a break inside Binance, an answer of its message bus
    // that it did not expect, and its own timeout.
    private static readonly int[] Unsaid = [-1000, -1001, -1006, -1007];

    // Binance's word that it has no such order: asked about, and asked to take off the exchange.
    private static readonly int[] NoSuchOrder = [-2013, -2011];

    // The first and the last moment that a count of milliseconds can name as a time.
    private static readonly long EarliestTime = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long LatestTime = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - 1;

    public async Task<ExchangeOrderResult> PlaceAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangeOrderRequest order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentNullException.ThrowIfNull(order);

        var id = Identifier(order.ClientOrderId);
        var side = order.Side == ExchangeOrderSide.Buy ? "BUY" : "SELL";
        var quantity = Positive(order.Quantity, nameof(order));
        var stop = order.Type == ExchangeOrderType.Stop;

        var endpoint = order.Type switch
        {
            ExchangeOrderType.Market => BinanceEndpoint.MarketOrderOf(market, order.Symbol, side, quantity, id, order.Closes),
            ExchangeOrderType.Stop => BinanceEndpoint.StopOrderOf(
                market,
                order.Symbol,
                side,
                quantity,
                Positive(order.TriggerPrice ?? 0m, nameof(order)),
                id,
                order.Closes),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order.Type, null),
        };

        var answer = await call.AskSignedAsync(endpoint, mode, userId, apiKey, secretKey, ReaderOf(market, stop), cancellationToken);

        return ResultOf(answer, places: true);
    }

    public async Task<ExchangeOrderResult> FindAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangeOrderRef order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentNullException.ThrowIfNull(order);

        var stop = order.Type == ExchangeOrderType.Stop;
        var endpoint = BinanceEndpoint.OrderStatusOf(market, order.Symbol, Identifier(order.ClientOrderId), stop);
        var answer = await call.AskSignedAsync(endpoint, mode, userId, apiKey, secretKey, ReaderOf(market, stop), cancellationToken);

        return ResultOf(answer, places: false);
    }

    public async Task<ExchangeOrderResult> CancelAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        ExchangeOrderRef order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentNullException.ThrowIfNull(order);

        var stop = order.Type == ExchangeOrderType.Stop;
        var endpoint = BinanceEndpoint.OrderCancelOf(market, order.Symbol, Identifier(order.ClientOrderId), stop);

        // Binance answers a conditional order that it took off with its id alone.
        Func<JsonElement, ExchangeOrder?> read = market == Market.Futures && stop ? TakenOff : ReaderOf(market, stop);
        var answer = await call.AskSignedAsync(endpoint, mode, userId, apiKey, secretKey, read, cancellationToken);

        return ResultOf(answer, places: false);
    }

    public async Task<ExchangeFills> FillsAsync(
        Guid userId,
        Market market,
        TradingMode mode,
        string apiKey,
        string secretKey,
        string symbol,
        string exchangeOrderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        ArgumentException.ThrowIfNullOrEmpty(exchangeOrderId);

        var answer = await call.AskSignedAsync(
            BinanceEndpoint.TradesOf(market, symbol, exchangeOrderId), mode, userId, apiKey, secretKey, Trades, cancellationToken);

        return new ExchangeFills(answer.Outcome, answer.Value ?? []);
    }

    /// <summary>
    /// What the end of a call says about the order. See the remarks of the class.
    /// </summary>
    /// <param name="answer">How the call ended, and what it was answered with.</param>
    /// <param name="places">Whether the call placed the order. Another call asked about it, or took it off.</param>
    private static ExchangeOrderResult ResultOf(BinanceAnswer<ExchangeOrder> answer, bool places)
    {
        switch (answer.Outcome)
        {
            case ExchangeCallOutcome.Succeeded:
                return new ExchangeOrderResult(ExchangeOrderOutcome.Answered, answer.Value);

            case ExchangeCallOutcome.AuthenticationFailed:
                return ExchangeOrderResult.Without(ExchangeOrderOutcome.KeyRefused);

            // The limiter held it back, or Binance answered that it has had too many: neither
            // reached the orders.
            case ExchangeCallOutcome.RateLimited:
                return ExchangeOrderResult.Without(ExchangeOrderOutcome.NotSent);

            case ExchangeCallOutcome.TimedOut:
                return ExchangeOrderResult.Without(ExchangeOrderOutcome.Unknown);
        }

        // No answer at all, an answer that says that the call went well and does not read, a
        // fault of Binance, or its own timeout: what became of the call is not known.
        if (answer.Status is not { } status
            || status is < 400 or >= 500 or 408
            || (answer.Code is { } unsaid && Unsaid.Contains(unsaid)))
        {
            return ExchangeOrderResult.Without(ExchangeOrderOutcome.Unknown);
        }

        // Binance's own word that it has no such order, to a call that asked about one.
        if (!places && answer.Code is { } code && NoSuchOrder.Contains(code))
        {
            return ExchangeOrderResult.Without(ExchangeOrderOutcome.NotFound);
        }

        return ExchangeOrderResult.Without(ExchangeOrderOutcome.Rejected);
    }

    /// <summary>What reads the answer about an order of a market: a conditional order of the Futures market reads differently.</summary>
    private static Func<JsonElement, ExchangeOrder?> ReaderOf(Market market, bool stop) => market switch
    {
        Market.Spot => SpotOrder,
        Market.Futures when stop => FuturesStop,
        Market.Futures => FuturesOrder,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    /// <summary>An order of the Spot market, or null when the answer is not one.</summary>
    private static ExchangeOrder? SpotOrder(JsonElement answer)
    {
        if (Number(answer, "orderId") is not { } id
            || StatusOf(Text(answer, "status")) is not { } status
            || Amount(answer, "executedQty") is not { } executed)
        {
            return null;
        }

        // What was paid or received for what is executed, in the asset that the symbol is priced in.
        var average = executed > 0m && Amount(answer, "cummulativeQuoteQty") is { } value and > 0m ? value / executed : (decimal?)null;

        // The full answer to an order that was placed has its executions. They say no time of
        // their own: it is the time of the order.
        List<ExchangeFill> fills = [];

        if (answer.TryGetProperty("fills", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            if (Moment(answer, "transactTime") is not { } at)
            {
                return null;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (Number(row, "tradeId") is not { } trade
                    || Amount(row, "price") is not { } price
                    || Amount(row, "qty") is not { } quantity
                    || Amount(row, "commission") is not { } fee
                    || Text(row, "commissionAsset") is not { Length: > 0 } asset)
                {
                    return null;
                }

                fills.Add(new ExchangeFill(trade, price, quantity, fee, asset, at));
            }
        }

        return new ExchangeOrder(id, status, executed, average, fills);
    }

    /// <summary>An order of the Futures market, or null when the answer is not one.</summary>
    private static ExchangeOrder? FuturesOrder(JsonElement answer)
    {
        if (Number(answer, "orderId") is not { } id
            || StatusOf(Text(answer, "status")) is not { } status
            || Amount(answer, "executedQty") is not { } executed)
        {
            return null;
        }

        var average = executed > 0m && Amount(answer, "avgPrice") is { } price and > 0m ? price : (decimal?)null;

        return new ExchangeOrder(id, status, executed, average, []);
    }

    /// <summary>
    /// A conditional order of the Futures market, or null when the answer is not one. Until it
    /// is triggered it rests, and nothing of it is executed. Once it is triggered it is the
    /// order that it became, which the answer names with what is executed of it.
    /// </summary>
    private static ExchangeOrder? FuturesStop(JsonElement answer)
    {
        if (Number(answer, "algoId") is not { } id || Text(answer, "algoStatus") is not { } said)
        {
            return null;
        }

        // The order that it became, when it was triggered, and what is executed of that.
        var became = Number(answer, "actualOrderId") is { Length: > 0 } actual && actual != "0" ? actual : null;
        var executed = Amount(answer, "actualQty") ?? 0m;
        var average = executed > 0m && Amount(answer, "actualPrice") is { } price and > 0m ? price : (decimal?)null;

        ExchangeOrderStatus? status = said switch
        {
            "NEW" => ExchangeOrderStatus.Open,
            "CANCELED" => ExchangeOrderStatus.Canceled,
            "EXPIRED" => ExchangeOrderStatus.Expired,
            "REJECTED" => ExchangeOrderStatus.Rejected,

            // Triggered, and its order is with the exchange.
            "TRIGGERING" or "TRIGGERED" => executed > 0m ? ExchangeOrderStatus.PartiallyFilled : ExchangeOrderStatus.Open,

            // Triggered, and over: executed in full, or ended with a part of it.
            "FINISHED" => Amount(answer, "quantity") is { } whole && executed >= whole && executed > 0m
                ? ExchangeOrderStatus.Filled
                : ExchangeOrderStatus.Expired,
            _ => null,
        };

        return status is { } known ? new ExchangeOrder(became ?? id, known, executed, average, []) : null;
    }

    /// <summary>A conditional order of the Futures market that Binance took off, or null when the answer is not that.</summary>
    private static ExchangeOrder? TakenOff(JsonElement answer) =>
        Number(answer, "algoId") is { } id ? new ExchangeOrder(id, ExchangeOrderStatus.Canceled, 0m, null, []) : null;

    /// <summary>
    /// The executions of an order, oldest first, or null when the answer is not their list or a
    /// row of it does not read: a part of a list would make the rest look as if it never happened.
    /// </summary>
    private static IReadOnlyList<ExchangeFill>? Trades(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fills = new List<ExchangeFill>();

        foreach (var row in answer.EnumerateArray())
        {
            if (Number(row, "id") is not { } trade
                || Amount(row, "price") is not { } price
                || Amount(row, "qty") is not { } quantity
                || Amount(row, "commission") is not { } fee
                || Text(row, "commissionAsset") is not { Length: > 0 } asset
                || Moment(row, "time") is not { } at)
            {
                return null;
            }

            fills.Add(new ExchangeFill(trade, price, quantity, fee, asset, at));
        }

        return [.. fills.OrderBy(fill => fill.At)];
    }

    /// <summary>What Binance's word for the state of an order says, or null when it is none that this code knows.</summary>
    private static ExchangeOrderStatus? StatusOf(string? said) => said switch
    {
        "NEW" or "PENDING_NEW" => ExchangeOrderStatus.Open,
        "PARTIALLY_FILLED" => ExchangeOrderStatus.PartiallyFilled,
        "FILLED" => ExchangeOrderStatus.Filled,
        "CANCELED" => ExchangeOrderStatus.Canceled,
        "REJECTED" => ExchangeOrderStatus.Rejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => ExchangeOrderStatus.Expired,
        _ => null,
    };

    /// <summary>The identifier of an order as it is sent. One that Binance would not take is an error of the caller.</summary>
    private static string Identifier(string clientOrderId) =>
        clientOrderId is not null && TakenByBinance().IsMatch(clientOrderId)
            ? clientOrderId
            : throw new ArgumentException(
                "The identifier of an order is 1 to 36 characters, of letters, digits, and . : / _ -", nameof(clientOrderId));

    /// <summary>An amount of an order as it is sent: in digits, with a point, whatever the culture.</summary>
    private static string Positive(decimal amount, string name) => amount > 0m
        ? amount.ToString(CultureInfo.InvariantCulture)
        : throw new ArgumentException("A quantity and a trigger price of an order are above zero.", name);

    /// <summary>What an answer says under a name as text, or null when it says nothing there as text.</summary>
    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A number that names something, which Binance writes as a number or as text, or null when there is none.</summary>
    private static string? Number(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>An amount, which Binance writes as text so that no digit is lost, or null when there is none.</summary>
    private static decimal? Amount(JsonElement body, string name) =>
        Text(body, name) is { } text
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                ? amount
                : null;

    /// <summary>The time that a count of milliseconds names, or null when there is none.</summary>
    private static DateTimeOffset? Moment(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var milliseconds)
            && milliseconds >= EarliestTime
            && milliseconds <= LatestTime
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;

    [GeneratedRegex(@"^[\.A-Z:/a-z0-9_-]{1,36}$")]
    private static partial Regex TakenByBinance();
}
