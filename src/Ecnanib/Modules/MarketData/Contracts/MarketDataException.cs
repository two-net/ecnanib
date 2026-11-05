namespace Ecnanib.Modules.MarketData.Contracts;

/// <summary>Why market data couldn't be had.</summary>
public enum MarketDataFailure
{
    /// <summary>Binance couldn't be reached, or answered with something other than data. Try again later.</summary>
    Unavailable,

    /// <summary>
    /// Not sent, to keep within the request-weight headroom, or because Binance refused or banned this
    /// server's address. Try again after <see cref="MarketDataException.RetryAfter"/>.
    /// </summary>
    Throttled,

    /// <summary>Binance doesn't know the symbol on this market and environment.</summary>
    UnknownSymbol,
}

/// <summary>
/// Market data that couldn't be had. The message names the server and Binance's error, never a URL or
/// a query. The exchange's own exception, when there is one, is the inner exception.
/// </summary>
public sealed class MarketDataException(MarketDataFailure failure, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public MarketDataFailure Failure { get; } = failure;

    /// <summary>For <see cref="MarketDataFailure.Throttled"/>: how long until a request may be sent again.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
