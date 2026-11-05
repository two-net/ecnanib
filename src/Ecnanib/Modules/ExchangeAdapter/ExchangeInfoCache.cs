using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// Exchange information, held in memory per market and environment, and refreshed once it's older than
/// Binance:ExchangeInfoRefreshHours (EXT-BIN-06). It loads lazily rather than on a schedule, because the
/// adapter can't use Platform's jobs. Market Data keeps a copy of the symbols. A failed refresh keeps the
/// last copy and waits a minute before trying again, so an outage doesn't turn every call into a request.
/// Each refresh also updates the servers' limits. Callers share one refresh, which none of them can
/// cancel, so one that stops waiting still gets the last copy when there is one.
/// </summary>
internal sealed class ExchangeInfoCache(
    BinanceTransport transport,
    BinanceServers servers,
    BinanceSettings settings,
    TimeProvider time,
    ILoggerFactory loggerFactory)
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    /// <summary>How old a copy must be before a rejection may expire it.</summary>
    public static readonly TimeSpan ExpireAfter = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(Market, TradingEnvironment), Entry> _entries = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger(BinanceTransport.Category);

    public async Task<ExchangeInfo> GetAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd((market, environment), _ => new Entry());
        Task<ExchangeInfo> refresh;

        lock (entry)
        {
            if (Usable(entry) is { } current)
            {
                return current;
            }

            refresh = entry.Refresh ??= RefreshAsync(entry, market, environment);
        }

        try
        {
            return await refresh.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && entry.Current is { } stale)
        {
            return stale;
        }
    }

    /// <summary>
    /// Marks the copy stale unless it's younger than <see cref="ExpireAfter"/>, so the next read fetches a new
    /// one (FR-RISK-04).
    /// </summary>
    public bool Expire(Market market, TradingEnvironment environment)
    {
        if (!_entries.TryGetValue((market, environment), out var entry))
        {
            return false;
        }

        lock (entry)
        {
            if (entry.Current is not { } current || time.GetUtcNow() - current.RefreshedAt < ExpireAfter)
            {
                return false;
            }

            // A refresh that failed less than a minute ago still holds the next one back.
            entry.Expired = true;
        }

        _logger.LogInformation(
            "Binance exchange information for {Market} {Environment} from {RefreshedAt:O} expired early, after a filter rejection",
            market,
            environment,
            entry.Current?.RefreshedAt);

        return true;
    }

    /// <summary>
    /// Fetches without any caller's token. The transport's own read timeout bounds it, and a public request
    /// has no outcome to lose.
    /// </summary>
    private async Task<ExchangeInfo> RefreshAsync(Entry entry, Market market, TradingEnvironment environment)
    {
        // So that the task is stored before it can finish and clear itself.
        await Task.Yield();

        try
        {
            var fetched = await FetchAsync(market, environment, CancellationToken.None);
            entry.Current = fetched;
            entry.Expired = false;

            return fetched;
        }
        catch (ExchangeException exception) when (entry.Current is { } current)
        {
            entry.NextAttempt = time.GetUtcNow() + RetryAfterFailure;
            _logger.LogWarning(
                exception,
                "Refreshing Binance exchange information for {Market} {Environment} failed, so the copy from {RefreshedAt:O} stays in use",
                market,
                environment,
                current.RefreshedAt);

            return current;
        }
        finally
        {
            lock (entry)
            {
                entry.Refresh = null;
            }
        }
    }

    /// <summary>The copy, while it's fresh, or while it's stale and the last refresh failed less than a minute ago.</summary>
    private ExchangeInfo? Usable(Entry entry)
    {
        var now = time.GetUtcNow();

        return entry.Current is { } current && ((!entry.Expired && now - current.RefreshedAt < settings.ExchangeInfoRefresh) || now < entry.NextAttempt)
            ? current
            : null;
    }

    private async Task<ExchangeInfo> FetchAsync(Market market, TradingEnvironment environment, CancellationToken cancellationToken)
    {
        var server = ExchangeServers.Public(market, environment);
        var endpoint = market == Market.Futures ? BinanceEndpoint.FuturesExchangeInfo : BinanceEndpoint.SpotExchangeInfo;

        // Permission sets are most of Spot's body, and nothing here uses them.
        IReadOnlyList<(string, string)> parameters = market == Market.Futures ? [] : [("showPermissionSets", "false")];

        var body = await transport.SendAsync<ExchangeInfoBody>(server, endpoint, parameters, key: null, RequestPriority.Normal, clientOrderId: null, cancellationToken);
        var (weightLimit, orderLimits) = Limits(body.RateLimits);

        servers[server].SetLimits(weightLimit, orderLimits);
        servers[ExchangeServers.Trading(market, environment)].SetLimits(weightLimit, orderLimits);

        var symbols = new Dictionary<string, SymbolRules>(StringComparer.Ordinal);

        foreach (var symbol in body.Symbols)
        {
            symbols[symbol.Symbol] = Rules(symbol);
        }

        return new ExchangeInfo(market, environment, time.GetUtcNow(), symbols);
    }

    /// <summary>The per-minute weight limit, and the order limits, which Spot calls ORDERS and Futures' documentation ORDER.</summary>
    private static (int? Weight, List<OrderLimit> Orders) Limits(IEnumerable<RateLimitBody> limits)
    {
        int? weight = null;
        var orders = new List<OrderLimit>();

        foreach (var limit in limits)
        {
            if (!ServerState.TryParseInterval($"{limit.IntervalNum.ToString(CultureInfo.InvariantCulture)}{IntervalLetter(limit.Interval)}", out var interval)
                || limit.Limit < 1)
            {
                continue;
            }

            if (limit.RateLimitType == "REQUEST_WEIGHT" && interval == TimeSpan.FromMinutes(1))
            {
                weight = limit.Limit;
            }
            else if (limit.RateLimitType is "ORDERS" or "ORDER")
            {
                orders.Add(new OrderLimit(interval, limit.Limit));
            }
        }

        return (weight, orders);
    }

    private static char IntervalLetter(string interval) => interval switch
    {
        "SECOND" => 'S',
        "MINUTE" => 'M',
        "HOUR" => 'H',
        "DAY" => 'D',
        _ => '?',
    };

    private static SymbolRules Rules(SymbolBody symbol)
    {
        PriceRules? price = null;
        QuantityRules? quantity = null;
        QuantityRules? marketQuantity = null;
        NotionalRules? notional = null;
        PercentPriceRules? percentPrice = null;

        foreach (var filter in symbol.Filters)
        {
            switch (Text(filter, "filterType"))
            {
                case "PRICE_FILTER" when Decimal(filter, "minPrice") is { } min && Decimal(filter, "maxPrice") is { } max && Decimal(filter, "tickSize") is { } tick:
                    price = new PriceRules(min, max, tick);
                    break;
                case "LOT_SIZE" when Quantity(filter) is { } lot:
                    quantity = lot;
                    break;
                case "MARKET_LOT_SIZE" when Quantity(filter) is { } lot:
                    marketQuantity = lot;
                    break;
                case "NOTIONAL" when Decimal(filter, "minNotional") is { } min:
                    notional = new NotionalRules(min, Decimal(filter, "maxNotional"));
                    break;
                case "MIN_NOTIONAL" when (Decimal(filter, "notional") ?? Decimal(filter, "minNotional")) is { } min:
                    notional = new NotionalRules(min, null);
                    break;

                // Spot's, by side, from the average price.
                case "PERCENT_PRICE_BY_SIDE"
                    when Decimal(filter, "bidMultiplierUp") is { } bidUp
                        && Decimal(filter, "bidMultiplierDown") is { } bidDown
                        && Decimal(filter, "askMultiplierUp") is { } askUp
                        && Decimal(filter, "askMultiplierDown") is { } askDown:
                    percentPrice = new PercentPriceRules(bidUp, bidDown, askUp, askDown, Whole(filter, "avgPriceMins"));
                    break;

                // Futures bounds a buy from above and a sell from below, around the mark price. Spot's older filter
                // bounds both sides both ways, around the average price, and gives way to the one by side.
                case "PERCENT_PRICE" when Decimal(filter, "multiplierUp") is { } up && Decimal(filter, "multiplierDown") is { } down:
                    if (symbol.ContractType is not null)
                    {
                        percentPrice = new PercentPriceRules(up, 0m, 0m, down, null);
                    }
                    else
                    {
                        percentPrice ??= new PercentPriceRules(up, down, up, down, Whole(filter, "avgPriceMins"));
                    }

                    break;
            }
        }

        return new SymbolRules(
            symbol.Symbol,
            symbol.Status,
            symbol.BaseAsset,
            symbol.QuoteAsset,
            symbol.ContractType,
            symbol.OrderTypes,
            price,
            quantity,
            marketQuantity,
            notional,
            percentPrice);
    }

    private static int? Whole(JsonElement filter, string name) =>
        filter.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static QuantityRules? Quantity(JsonElement filter) =>
        Decimal(filter, "minQty") is { } min && Decimal(filter, "maxQty") is { } max && Decimal(filter, "stepSize") is { } step
            ? new QuantityRules(min, max, step)
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Binance sends decimals as strings, and occasionally as numbers.</summary>
    private static decimal? Decimal(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            _ => null,
        };
    }

    private sealed class Entry
    {
        /// <summary>The refresh under way, which every caller waits for.</summary>
        public Task<ExchangeInfo>? Refresh { get; set; }

        public ExchangeInfo? Current { get; set; }

        public DateTimeOffset NextAttempt { get; set; }

        /// <summary>Set by <see cref="Expire"/>, until the next fetch succeeds.</summary>
        public bool Expired { get; set; }
    }
}
