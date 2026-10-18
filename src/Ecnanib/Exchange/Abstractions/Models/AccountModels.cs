namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Permissions of an API key as reported by Binance (FR-KEY-02). <see cref="Verified"/> is false when the exchange
/// environment cannot report permissions (the Spot testnet has no such endpoint); the key module decides what that means.
/// </summary>
public sealed record ApiKeyPermissions(
    bool Verified,
    bool EnableReading,
    bool EnableSpotTrading,
    bool EnableFutures,
    bool EnableWithdrawals,
    bool IpRestricted,
    DateTimeOffset? CreatedAt)
{
    public static ApiKeyPermissions Unverified { get; } = new(false, false, false, false, false, false, null);
}

public sealed record AssetBalance(string Asset, decimal Available, decimal Locked, decimal Total, decimal? UnrealizedPnl);

public sealed record FuturesAccountSummary(
    decimal TotalWalletBalance,
    decimal TotalMarginBalance,
    decimal TotalUnrealizedPnl,
    decimal AvailableBalance,
    DateTimeOffset Timestamp);

/// <summary>A one-way-mode futures position. <see cref="Quantity"/> is signed: positive long, negative short.</summary>
public sealed record FuturesPosition(
    string Symbol,
    decimal Quantity,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal UnrealizedPnl,
    decimal? LiquidationPrice,
    int Leverage,
    MarginType MarginType,
    decimal? IsolatedMargin,
    decimal Notional,
    DateTimeOffset UpdatedAt)
{
    public bool IsOpen => Quantity != 0;

    public OrderSide? Side => Quantity switch
    {
        > 0 => OrderSide.Buy,
        < 0 => OrderSide.Sell,
        _ => null,
    };
}
