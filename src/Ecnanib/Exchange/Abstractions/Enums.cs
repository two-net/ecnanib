namespace Ecnanib.Exchange.Abstractions;

/// <summary>Binance market a client or stream operates on. Only Spot and USDT-margined Futures are supported (C-02).</summary>
public enum MarketType
{
    Spot = 1,
    UsdFutures = 2,
}

/// <summary>Exchange environment, selected per exchange key (EX-04).</summary>
public enum ExchangeEnvironment
{
    Production = 1,
    Testnet = 2,
}

/// <summary>Candle intervals the platform uses: the four agent timeframes plus 1W for higher-timeframe bias (FR-STR-03).</summary>
public enum CandleInterval
{
    FifteenMinutes = 1,
    OneHour = 2,
    FourHours = 3,
    OneDay = 4,
    OneWeek = 5,
}

public enum OrderSide
{
    Buy = 1,
    Sell = 2,
}

public enum OrderType
{
    Limit = 1,
    Market = 2,
    /// <summary>Futures STOP_MARKET (spot STOP_LOSS maps here too).</summary>
    StopMarket = 3,
    /// <summary>Futures TAKE_PROFIT_MARKET (spot TAKE_PROFIT maps here too).</summary>
    TakeProfitMarket = 4,
    /// <summary>Spot STOP_LOSS_LIMIT / futures STOP.</summary>
    StopLossLimit = 5,
    /// <summary>Spot TAKE_PROFIT_LIMIT / futures TAKE_PROFIT.</summary>
    TakeProfitLimit = 6,
    LimitMaker = 7,
    Other = 99,
}

public enum TimeInForce
{
    GoodTillCanceled = 1,
    ImmediateOrCancel = 2,
    FillOrKill = 3,
    Other = 99,
}

public enum OrderStatus
{
    New = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Canceled = 4,
    PendingCancel = 5,
    Rejected = 6,
    Expired = 7,
    Other = 99,
}

public enum ExecutionType
{
    New = 1,
    Canceled = 2,
    Trade = 3,
    Expired = 4,
    Rejected = 5,
    Other = 99,
}

public enum MarginType
{
    Isolated = 1,
    Cross = 2,
}

public enum PositionMode
{
    OneWay = 1,
    Hedge = 2,
}

public enum WorkingType
{
    MarkPrice = 1,
    ContractPrice = 2,
}

public enum OcoListStatus
{
    Executing = 1,
    AllDone = 2,
    Rejected = 3,
    Other = 99,
}

public enum RateLimitKind
{
    RequestWeight = 1,
    Orders = 2,
    RawRequests = 3,
    Other = 99,
}

/// <summary>Health of a market or user-data stream (NFR-OBS-01).</summary>
public enum StreamHealth
{
    /// <summary>Live WebSocket delivery.</summary>
    Connected = 1,
    /// <summary>Stream unavailable; REST polling fallback is active and delivering (EX-03).</summary>
    Polling = 2,
    /// <summary>Stream unavailable and the last poll failed.</summary>
    Down = 3,
    Stopped = 4,
}

public enum ExchangeErrorKind
{
    Unknown = 0,
    Network,
    Timeout,
    Unauthorized,
    RateLimited,
    InvalidParameter,
    InvalidTimestamp,
    UnknownSymbol,
    UnknownOrder,
    DuplicateClientOrderId,
    InvalidQuantity,
    InvalidPrice,
    InvalidStop,
    InsufficientBalance,
    RejectedOrder,
    NoPosition,
    IncorrectState,
    SystemError,
}

public static class CandleIntervalExtensions
{
    /// <summary>1970-01-05 is the first Monday after the Unix epoch (which was a Thursday).</summary>
    private static readonly DateTimeOffset WeekEpoch = new(1970, 1, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// UTC-aligned open time of the candle that contains <paramref name="time"/> (FR-AGT-09). Weekly candles open on
    /// Monday 00:00 UTC, as on Binance.
    /// </summary>
    public static DateTimeOffset OpenTimeOf(this CandleInterval interval, DateTimeOffset time)
    {
        var span = interval.ToTimeSpan();
        var epoch = interval == CandleInterval.OneWeek ? WeekEpoch : DateTimeOffset.UnixEpoch;
        var elapsed = (time.ToUniversalTime() - epoch).Ticks;
        var remainder = elapsed % span.Ticks;
        if (remainder < 0)
            remainder += span.Ticks;
        return epoch + TimeSpan.FromTicks(elapsed - remainder);
    }

    /// <summary>The first candle boundary strictly after <paramref name="time"/>; at a boundary this is the one after it.</summary>
    public static DateTimeOffset NextCloseAfter(this CandleInterval interval, DateTimeOffset time) =>
        interval.OpenTimeOf(time) + interval.ToTimeSpan();

    public static TimeSpan ToTimeSpan(this CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
        CandleInterval.OneHour => TimeSpan.FromHours(1),
        CandleInterval.FourHours => TimeSpan.FromHours(4),
        CandleInterval.OneDay => TimeSpan.FromDays(1),
        CandleInterval.OneWeek => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unsupported candle interval."),
    };

    /// <summary>Binance-style label: 15m, 1h, 4h, 1d, 1w.</summary>
    public static string ToLabel(this CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.OneHour => "1h",
        CandleInterval.FourHours => "4h",
        CandleInterval.OneDay => "1d",
        CandleInterval.OneWeek => "1w",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unsupported candle interval."),
    };
}

public static class OrderStatusExtensions
{
    public static bool IsOpen(this OrderStatus status) => status is OrderStatus.New or OrderStatus.PartiallyFilled;

    public static bool IsTerminal(this OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Rejected or OrderStatus.Expired;
}
