namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Authenticated access to one market with one exchange key (EX-01, EX-02). Order placement and cancellation are
/// never retried internally; the caller relies on deterministic client order ids (FR-EXE-07).
/// </summary>
public interface IExchangeAccountClient
{
    string KeyId { get; }

    ExchangeEnvironment Environment { get; }

    MarketType Market { get; }

    /// <summary>Makes an authenticated account request; throws <see cref="ExchangeAuthException"/> for an invalid key (FR-KEY-02, FR-KEY-07).</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Reads the key's permissions. Returns <see cref="ApiKeyPermissions.Unverified"/> on environments without the endpoint.</summary>
    Task<ApiKeyPermissions> GetApiKeyPermissionsAsync(CancellationToken ct);

    Task<IReadOnlyList<AssetBalance>> GetBalancesAsync(CancellationToken ct);

    Task<ExchangeOrder> PlaceOrderAsync(OrderRequest request, CancellationToken ct);

    Task<ExchangeOrder> GetOrderAsync(string symbol, OrderRef order, CancellationToken ct);

    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct);

    /// <summary>Cancels one order. An unknown order surfaces as <see cref="ExchangeRequestException"/> with kind <see cref="ExchangeErrorKind.UnknownOrder"/>.</summary>
    Task<ExchangeOrder> CancelOrderAsync(string symbol, OrderRef order, CancellationToken ct);

    Task<IReadOnlyList<ExchangeOrder>> CancelAllOrdersAsync(string symbol, CancellationToken ct);

    /// <summary>The key's fills for a symbol: fees and realized P&amp;L come from here (FR-EXE-12).</summary>
    Task<IReadOnlyList<UserTrade>> GetTradesAsync(
        string symbol, DateTimeOffset? from, long? fromTradeId, int? limit, CancellationToken ct);

    /// <summary>
    /// Subscribes to the key's user-data stream for this market (orders, fills, balances, positions), with REST
    /// polling fallback (EX-03). The session is shared by all subscribers of the same key and market.
    /// </summary>
    IDisposable SubscribeUserData(Func<UserDataEvent, CancellationToken, ValueTask> handler);

    StreamStatus UserDataStatus { get; }
}

/// <summary>Spot additions: OCO exits (FR-EXE-01). Spot agents are long-only (FR-AGT-12).</summary>
public interface ISpotAccountClient : IExchangeAccountClient
{
    Task<OcoOrderList> PlaceOcoAsync(SpotOcoRequest request, CancellationToken ct);

    Task<OcoOrderList> CancelOcoAsync(string symbol, string listClientOrderId, CancellationToken ct);

    Task<OcoOrderList> GetOcoAsync(string listClientOrderId, CancellationToken ct);
}

/// <summary>USD-M futures additions: positions and position settings (EX-02, FR-EXE-11). One-way mode only (A-06).</summary>
public interface IFuturesAccountClient : IExchangeAccountClient
{
    Task<FuturesAccountSummary> GetAccountSummaryAsync(CancellationToken ct);

    Task<IReadOnlyList<FuturesPosition>> GetPositionsAsync(string? symbol, CancellationToken ct);

    Task<PositionMode> GetPositionModeAsync(CancellationToken ct);

    /// <summary>Sets the account position mode. "No need to change" responses count as success.</summary>
    Task SetPositionModeAsync(PositionMode mode, CancellationToken ct);

    /// <summary>Sets initial leverage for a symbol and returns the leverage the exchange applied.</summary>
    Task<int> SetLeverageAsync(string symbol, int leverage, CancellationToken ct);

    /// <summary>Sets the margin type for a symbol. "No need to change" responses count as success.</summary>
    Task SetMarginTypeAsync(string symbol, MarginType marginType, CancellationToken ct);
}
