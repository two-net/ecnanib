namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>Why an exchange call failed, which decides whether it may have done anything.</summary>
public enum ExchangeFailure
{
    /// <summary>Binance answered with an error, so nothing happened. <see cref="ExchangeException.Code"/> says why.</summary>
    Rejected,

    /// <summary>
    /// Not sent, or refused by Binance with HTTP 429, so nothing happened. Try again after
    /// <see cref="ExchangeException.RetryAfter"/> (EXT-BIN-04).
    /// </summary>
    Throttled,

    /// <summary>
    /// Not sent, or refused with HTTP 418, because Binance has banned this server's address. Every request
    /// to that server stops until the ban ends (EXT-BIN-04).
    /// </summary>
    Banned,

    /// <summary>
    /// The request was sent, but the outcome isn't known: a server error, a timeout, or a broken
    /// connection (EXT-BIN-05). An order may have been placed, and may still be until
    /// <see cref="ExchangeException.SettlesAt"/>. Query it by its client order ID before sending it again
    /// (FR-EXE-05).
    /// </summary>
    Unknown,

    /// <summary>
    /// Not sent, because the server's clock couldn't be synced first, so nothing happened. Try again
    /// later.
    /// </summary>
    Unavailable,
}

/// <summary>
/// An exchange call that failed. The message never holds a URL, a query, a key or a signature, only
/// the server, Binance's error code and its message.
/// </summary>
public sealed class ExchangeException(
    ExchangeServer server,
    ExchangeFailure failure,
    string message,
    int? code = null,
    TimeSpan? retryAfter = null,
    Exception? innerException = null,
    DateTimeOffset? settlesAt = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// For an order with an unknown outcome: when it can no longer take effect, by this server's clock.
    /// Binance accepts a signed request until its receive window has passed, so until then a query that
    /// finds nothing doesn't prove the order was never placed. <see cref="IExchangeAccount.GetOrderAsync"/>
    /// refuses to say it doesn't exist before then, within this process. Persist it with the order if
    /// that must hold across a restart.
    /// </summary>
    public DateTimeOffset? SettlesAt { get; } = settlesAt;

    public ExchangeServer Server { get; } = server;

    public ExchangeFailure Failure { get; } = failure;

    /// <summary>Binance's error code, such as -2010, when it gave one.</summary>
    public int? Code { get; } = code;

    /// <summary>How long until a throttled or banned request may be sent again.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>True when the request may have taken effect, so an order must be queried before it's sent again.</summary>
    public bool MayHaveExecuted => Failure == ExchangeFailure.Unknown;

    /// <summary>
    /// True when Binance rejected an order on one of the symbol's filters, so Ecnanib's copy of them may be out
    /// of date. Execution then refreshes it with <c>IExchangeSymbols.RefreshAsync</c> (FR-RISK-04).
    /// </summary>
    public bool IsFilterRejection => Failure == ExchangeFailure.Rejected && Code is { } code && FilterCodes.Contains(code);

    /// <summary>
    /// From Binance's error code pages: Spot's -1013 "Filter failure" (any filter, named in the message) and
    /// -1111 precision, and Futures' price, quantity, tick, step, percent-price and minimum-notional codes.
    /// Check them against Demo when orders are first placed (#34).
    /// </summary>
    private static readonly HashSet<int> FilterCodes =
    [
        -1013, // Filter failure (Spot)
        -1111, // Precision is over the maximum defined for this asset
        -4001, // Price less than 0
        -4002, // Price greater than max price
        -4003, // Quantity less than zero
        -4004, // Quantity less than min quantity
        -4005, // Quantity greater than max quantity
        -4006, // Stop price less than zero
        -4007, // Stop price greater than max price
        -4013, // Price less than min price
        -4014, // Price not increased by tick size
        -4016, // Limit price can't be higher than the multiplier-up bound
        -4023, // Quantity not increased by step size
        -4024, // Limit price can't be lower than the multiplier-down bound
        -4131, // The counterparty's best price doesn't meet the PERCENT_PRICE filter
        -4164, // Notional must be no smaller than the minimum
    ];
}
