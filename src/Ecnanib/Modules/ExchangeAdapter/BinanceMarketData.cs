using System.Globalization;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// Public market data. Exchange information comes from <see cref="ExchangeInfoCache"/>. Everything else is
/// fetched on each call, because sharing it between agents is Market Data's job (FR-CYC-09).
/// </summary>
internal sealed class BinanceMarketData(BinanceTransport transport, ExchangeInfoCache exchangeInfo, TimeProvider time) : IExchangeMarketData
{
    public Task<ExchangeInfo> GetExchangeInfoAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken) =>
        exchangeInfo.GetAsync(market, environment, cancellationToken);

    public bool ExpireExchangeInfo(Market market, TradingEnvironment environment) => exchangeInfo.Expire(market, environment);

    public async Task<IReadOnlyList<ExchangeCandle>> GetCandlesAsync(
        Market market,
        TradingEnvironment environment,
        string symbol,
        CandleInterval interval,
        DateTimeOffset? from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken)
    {
        CheckSymbol(symbol);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IExchangeMarketData.MaximumCandles);

        if (from > to)
        {
            throw new ArgumentException("The first open time is after the last.", nameof(from));
        }

        var server = ExchangeServers.Public(market, environment);
        var endpoint = market == Market.Futures ? BinanceEndpoint.FuturesKlines(limit) : BinanceEndpoint.SpotKlines;
        List<(string, string)> parameters = [("symbol", symbol), ("interval", Code(interval))];

        // Binance filters by open time, including both ends.
        if (from is { } start)
        {
            parameters.Add(("startTime", Milliseconds(start)));
        }

        parameters.Add(("endTime", Milliseconds(to)));
        parameters.Add(("limit", limit.ToString(CultureInfo.InvariantCulture)));

        var body = await transport.SendAsync(server, endpoint, parameters, key: null, RequestPriority.Normal, clientOrderId: null, cancellationToken);

        try
        {
            return ReadCandles(body, from, to);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            throw Unreadable(server, endpoint, exception);
        }
    }

    public async Task<ExchangeQuote> GetQuoteAsync(Market market, TradingEnvironment environment, string symbol, CancellationToken cancellationToken)
    {
        CheckSymbol(symbol);
        var server = ExchangeServers.Public(market, environment);
        IReadOnlyList<(string, string)> parameters = [("symbol", symbol)];

        if (market == Market.Spot)
        {
            var ticker = await transport.SendAsync<SpotTicker24hBody>(
                server, BinanceEndpoint.SpotTicker24h, parameters, key: null, RequestPriority.Normal, clientOrderId: null, cancellationToken);

            return new ExchangeQuote(symbol, ticker.LastPrice, ticker.BidPrice, ticker.BidQty, ticker.AskPrice, ticker.AskQty, time.GetUtcNow());
        }

        var book = transport.SendAsync<FuturesBookTickerBody>(
            server, BinanceEndpoint.FuturesBookTicker, parameters, key: null, RequestPriority.Normal, clientOrderId: null, cancellationToken);
        var price = transport.SendAsync<FuturesPriceBody>(
            server, BinanceEndpoint.FuturesPrice, parameters, key: null, RequestPriority.Normal, clientOrderId: null, cancellationToken);
        await Task.WhenAll(book, price);

        var (top, last) = (await book, await price);

        return new ExchangeQuote(symbol, last.Price, top.BidPrice, top.BidQty, top.AskPrice, top.AskQty, time.GetUtcNow());
    }

    public async Task<ExchangePremiumIndex> GetPremiumIndexAsync(TradingEnvironment environment, string symbol, CancellationToken cancellationToken)
    {
        CheckSymbol(symbol);

        var body = await transport.SendAsync<PremiumIndexBody>(
            ExchangeServers.Public(Market.Futures, environment),
            BinanceEndpoint.FuturesPremiumIndex,
            [("symbol", symbol)],
            key: null,
            RequestPriority.Normal,
            clientOrderId: null,
            cancellationToken);

        var fundingRate = decimal.TryParse(body.LastFundingRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : (decimal?)null;
        var nextFunding = body.NextFundingTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(body.NextFundingTime) : (DateTimeOffset?)null;

        return new ExchangePremiumIndex(symbol, body.MarkPrice, body.IndexPrice, fundingRate, nextFunding, time.GetUtcNow());
    }

    public async Task<IReadOnlyDictionary<string, int>> GetFundingIntervalsAsync(TradingEnvironment environment, CancellationToken cancellationToken)
    {
        var symbols = await transport.SendAsync<List<FundingInfoBody>>(
            ExchangeServers.Public(Market.Futures, environment),
            BinanceEndpoint.FuturesFundingInfo,
            [],
            key: null,
            RequestPriority.Normal,
            clientOrderId: null,
            cancellationToken);

        // A symbol listed only for its cap or floor still has an interval. One without is left to the default.
        return symbols
            .Where(symbol => symbol.FundingIntervalHours > 0)
            .GroupBy(symbol => symbol.Symbol, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().FundingIntervalHours, StringComparer.Ordinal);
    }

    internal static string Code(CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.OneHour => "1h",
        CandleInterval.FourHours => "4h",
        CandleInterval.OneDay => "1d",
        CandleInterval.OneWeek => "1w",
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };

    internal static void CheckSymbol(string symbol)
    {
        if (!OrderRequest.IsSymbol(symbol))
        {
            throw new ArgumentException("A symbol is upper-case letters and digits, such as BTCUSDT.", nameof(symbol));
        }
    }

    /// <summary>
    /// Each candle is an array: open time, open, high, low, close, volume, close time, quote volume, trades,
    /// and fields this version doesn't use. Prices and volumes are strings, read into decimal (DATA-R-02).
    /// </summary>
    private static List<ExchangeCandle> ReadCandles(byte[] body, DateTimeOffset? from, DateTimeOffset to)
    {
        using var document = JsonDocument.Parse(body);
        var candles = new List<ExchangeCandle>();

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.GetArrayLength() < 9)
            {
                throw new FormatException("A candle has fewer fields than Binance documents.");
            }

            var candle = new ExchangeCandle(
                DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()),
                Decimal(row[1]),
                Decimal(row[2]),
                Decimal(row[3]),
                Decimal(row[4]),
                Decimal(row[5]),
                DateTimeOffset.FromUnixTimeMilliseconds(row[6].GetInt64()),
                Decimal(row[7]),
                row[8].GetInt64());

            if (candles.Count > 0 && candle.OpenTime <= candles[^1].OpenTime)
            {
                throw new FormatException("Candles aren't in order of open time.");
            }

            // The range is Binance's to apply, but nothing outside it is ever passed on.
            if (candle.OpenTime >= (from ?? DateTimeOffset.MinValue) && candle.OpenTime <= to)
            {
                candles.Add(candle);
            }
        }

        return candles;
    }

    private static decimal Decimal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValueKind.Number => value.GetDecimal(),
        _ => throw new FormatException("A candle's price or volume isn't a number."),
    };

    private static string Milliseconds(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    internal static ExchangeException Unreadable(ExchangeServer server, BinanceEndpoint endpoint, Exception exception) =>
        new(
            server,
            ExchangeFailure.Rejected,
            $"{ExchangeServers.Describe(server)} {endpoint.Path} returned a body this version can't read.",
            innerException: exception);
}
