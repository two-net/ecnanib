namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Events delivered by a key's user-data stream, or by its REST polling fallback. The execution module uses
/// <see cref="Snapshot"/> and <see cref="StreamRestored"/> as reconciliation triggers (FR-EXE-08).
/// </summary>
public abstract record UserDataEvent(string KeyId, MarketType Market, DateTimeOffset Timestamp)
{
    public sealed record OrderUpdate(
        string KeyId, MarketType Market, DateTimeOffset Timestamp,
        ExchangeOrder Order, ExecutionType Execution, UserTrade? Fill) : UserDataEvent(KeyId, Market, Timestamp);

    public sealed record OcoUpdate(
        string KeyId, MarketType Market, DateTimeOffset Timestamp,
        OcoOrderList List) : UserDataEvent(KeyId, Market, Timestamp);

    public sealed record BalanceUpdate(
        string KeyId, MarketType Market, DateTimeOffset Timestamp,
        IReadOnlyList<AssetBalance> Balances) : UserDataEvent(KeyId, Market, Timestamp);

    /// <summary>Futures ACCOUNT_UPDATE positions. Stream positions carry no mark or liquidation price.</summary>
    public sealed record PositionUpdate(
        string KeyId, MarketType Market, DateTimeOffset Timestamp,
        IReadOnlyList<FuturesPosition> Positions, string Reason) : UserDataEvent(KeyId, Market, Timestamp);

    public sealed record LeverageUpdate(
        string KeyId, MarketType Market, DateTimeOffset Timestamp,
        string Symbol, int Leverage) : UserDataEvent(KeyId, Market, Timestamp);

    /// <summary>Full REST snapshot, emitted by the polling fallback and once after every stream restore.</summary>
    public sealed record Snapshot(
        string KeyId, MarketType Market, DateTimeOffset Timestamp,
        IReadOnlyList<ExchangeOrder> OpenOrders,
        IReadOnlyList<FuturesPosition>? Positions,
        IReadOnlyList<AssetBalance> Balances) : UserDataEvent(KeyId, Market, Timestamp);

    public sealed record StreamLost(string KeyId, MarketType Market, DateTimeOffset Timestamp)
        : UserDataEvent(KeyId, Market, Timestamp);

    public sealed record StreamRestored(string KeyId, MarketType Market, DateTimeOffset Timestamp, TimeSpan Offline)
        : UserDataEvent(KeyId, Market, Timestamp);
}
