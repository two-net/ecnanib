using Ecnanib.Kernel;

namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// Signed reads of a user's Binance account. Credentials (#20) validates keys with it, and Execution
/// reconciles with it. Placing and cancelling orders is <see cref="IExchangeTrading"/>, which only
/// Execution may use. See Exchange adapter in docs/architecture.md.
/// </summary>
public interface IExchangeAccount
{
    /// <summary>The account behind <paramref name="key"/>, on the key's environment (FR-KEY-04), at normal priority.</summary>
    /// <exception cref="ExchangeException">The call failed. An invalid key is <see cref="ExchangeFailure.Rejected"/>.</exception>
    /// <exception cref="ArgumentException">An Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.</exception>
    Task<ExchangeAccount> GetAccountAsync(Market market, ExchangeKey key, CancellationToken cancellationToken) =>
        GetAccountAsync(market, key, RequestPriority.Normal, cancellationToken);

    /// <summary>
    /// The account behind <paramref name="key"/>. Use <see cref="RequestPriority.Protective"/> when closing a position
    /// depends on it (FR-EXE-09).
    /// </summary>
    /// <exception cref="ExchangeException">The call failed. An invalid key is <see cref="ExchangeFailure.Rejected"/>.</exception>
    /// <exception cref="ArgumentException">An Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.</exception>
    Task<ExchangeAccount> GetAccountAsync(Market market, ExchangeKey key, RequestPriority priority, CancellationToken cancellationToken);

    /// <summary>
    /// What a Live key is allowed to do (FR-KEY-05), from <c>GET /sapi/v1/account/apiRestrictions</c> on the
    /// Spot Live server. Binance documents this endpoint for Live only, so a Demo key is refused.
    /// </summary>
    /// <exception cref="ExchangeException">
    /// The call failed. An invalid key, or an address the key's IP restriction leaves out, is
    /// <see cref="ExchangeFailure.Rejected"/>: Binance gives both -2015.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A Demo key, or an Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.
    /// </exception>
    Task<ApiRestrictions> GetApiRestrictionsAsync(ExchangeKey key, CancellationToken cancellationToken);

    /// <summary>
    /// An order by its client order ID, or null when Binance says it doesn't exist and it can't appear
    /// later. This is the query that settles an unknown outcome before anything is sent again
    /// (FR-EXE-05): only null allows that. Use <see cref="RequestPriority.Protective"/> when it's about a
    /// protective order.
    /// </summary>
    /// <exception cref="ExchangeException">
    /// The call failed, which says nothing about whether the order exists. It's
    /// <see cref="ExchangeFailure.Unknown"/> with <see cref="ExchangeException.SettlesAt"/> when Binance has
    /// no such order yet, but a send of it with an unknown outcome could still be accepted.
    /// </exception>
    Task<ExchangeOrder?> GetOrderAsync(
        Market market,
        ExchangeKey key,
        OrderReference order,
        RequestPriority priority,
        CancellationToken cancellationToken);

    /// <summary>
    /// A Spot order list by its client order ID, or null when Binance says it doesn't exist and it can't appear later,
    /// with the same rules as <see cref="GetOrderAsync"/>. Binance gives each order's ID, but not its state.
    /// </summary>
    /// <exception cref="ExchangeException">The call failed, which says nothing about whether the list exists.</exception>
    Task<ExchangeOrderList?> GetOrderListAsync(
        ExchangeKey key,
        OrderListReference list,
        RequestPriority priority,
        CancellationToken cancellationToken);

    /// <summary>
    /// The trades that filled one order, oldest first, with their commissions: <c>myTrades</c> on Spot and
    /// <c>userTrades</c> on Futures. Spot takes a buy's commission from the base asset bought, unless BNB pays it,
    /// so what a Spot position holds is the executed quantity less that (FR-AGT-09).
    /// </summary>
    /// <param name="exchangeOrderId">Binance's ID for the order, not its client order ID.</param>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept. Nothing was sent.</exception>
    /// <exception cref="ExchangeException">The call failed.</exception>
    Task<IReadOnlyList<ExchangeTrade>> GetOrderTradesAsync(
        Market market,
        ExchangeKey key,
        string symbol,
        long exchangeOrderId,
        RequestPriority priority,
        CancellationToken cancellationToken);

    /// <summary>
    /// The key's open Futures positions in one symbol, with their liquidation prices (FR-CYC-02), from
    /// <c>GET /fapi/v3/positionRisk</c>. It asks about the one symbol rather than the whole account
    /// (NFR-PERF-05). Empty when there's no position. In one-way mode there's at most one, on side BOTH.
    /// Use <see cref="RequestPriority.Protective"/> when a protective order depends on it.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept, or an Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.</exception>
    /// <exception cref="ExchangeException">The call failed. An invalid key is <see cref="ExchangeFailure.Rejected"/>.</exception>
    Task<IReadOnlyList<FuturesPosition>> GetPositionAsync(
        ExchangeKey key,
        string symbol,
        RequestPriority priority,
        CancellationToken cancellationToken);

    /// <summary>
    /// The Futures account's settings that EXT-BIN-07 and FR-RISK-09 depend on, from
    /// <c>GET /fapi/v1/accountConfig</c>: whether it may trade, its position mode and its Multi-Assets mode.
    /// </summary>
    /// <exception cref="ArgumentException">An Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.</exception>
    /// <exception cref="ExchangeException">The call failed. An invalid key, or one Futures isn't enabled for, is <see cref="ExchangeFailure.Rejected"/>.</exception>
    Task<FuturesAccountConfig> GetFuturesAccountConfigAsync(ExchangeKey key, CancellationToken cancellationToken);

    /// <summary>
    /// One symbol's margin type and leverage on the key's Futures account, from <c>GET /fapi/v1/symbolConfig</c>.
    /// Null when Binance lists nothing for it.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept, or an Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.</exception>
    /// <exception cref="ExchangeException">The call failed.</exception>
    Task<FuturesSymbolConfig?> GetFuturesSymbolConfigAsync(ExchangeKey key, string symbol, CancellationToken cancellationToken);

    /// <summary>
    /// One symbol's notional and leverage brackets for the key's account, lowest first, from
    /// <c>GET /fapi/v1/leverageBracket</c>. Binance can adjust them per account, which is why it's signed. Empty
    /// when Binance lists none for the symbol.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept, or an Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.</exception>
    /// <exception cref="ExchangeException">The call failed.</exception>
    Task<IReadOnlyList<ExchangeLeverageBracket>> GetLeverageBracketsAsync(ExchangeKey key, string symbol, CancellationToken cancellationToken);
}

/// <summary>A Futures account's settings, from <c>GET /fapi/v1/accountConfig</c>.</summary>
/// <param name="CanTrade">Whether Binance lets the account trade Futures.</param>
/// <param name="HedgeMode">
/// True in Hedge Mode, where a symbol can hold a long and a short at once. Ecnanib needs One-way Mode
/// (FR-RISK-09). Binance calls it <c>dualSidePosition</c>.
/// </param>
/// <param name="MultiAssetsMode">
/// True in Multi-Assets Mode, where Binance allows cross margin only, so a symbol can't be set to isolated
/// (Binance's -4168).
/// </param>
public sealed record FuturesAccountConfig(bool CanTrade, bool HedgeMode, bool MultiAssetsMode);

/// <summary>How a Futures symbol's position is margined.</summary>
public enum FuturesMarginType
{
    /// <summary>The position's margin is its own, so a liquidation loses only that. The only type Ecnanib sets (FR-RISK-02).</summary>
    Isolated,

    /// <summary>The position shares the account's margin.</summary>
    Crossed,
}

/// <summary>A symbol's settings on a Futures account, from <c>GET /fapi/v1/symbolConfig</c>.</summary>
/// <param name="MarginType">Null when it's a type this version doesn't know.</param>
public sealed record FuturesSymbolConfig(string Symbol, FuturesMarginType? MarginType, int Leverage);

/// <summary>One of a symbol's leverage brackets on a Futures account, from <c>GET /fapi/v1/leverageBracket</c>.</summary>
/// <param name="Bracket">Binance's number for it, from 1.</param>
/// <param name="InitialLeverage">The highest leverage a position in this bracket may use.</param>
/// <param name="NotionalFloor">The bracket applies from this position notional.</param>
/// <param name="NotionalCap">Up to this one.</param>
/// <param name="MaintenanceMarginRate">As a fraction, such as 0.004. Binance's <c>maintMarginRatio</c>.</param>
/// <param name="MaintenanceAmount">Binance's <c>cum</c>, which the liquidation price needs.</param>
public sealed record ExchangeLeverageBracket(
    int Bracket,
    int InitialLeverage,
    decimal NotionalFloor,
    decimal NotionalCap,
    decimal MaintenanceMarginRate,
    decimal MaintenanceAmount);

/// <summary>An open Futures position, as Binance last reported it. Prices and profit are in the margin asset.</summary>
/// <param name="Side">BOTH in one-way mode, or LONG or SHORT in hedge mode.</param>
/// <param name="Amount">The size in the base asset: positive for a long and negative for a short.</param>
/// <param name="BreakEvenPrice">Null when Binance sends none.</param>
/// <param name="LiquidationPrice">Null when Binance sends 0, which means the position can't be liquidated at any price.</param>
/// <param name="UpdatedAt">Null when Binance sends none.</param>
public sealed record FuturesPosition(
    string Symbol,
    string Side,
    decimal Amount,
    decimal EntryPrice,
    decimal? BreakEvenPrice,
    decimal MarkPrice,
    decimal? LiquidationPrice,
    decimal UnrealizedProfit,
    string MarginAsset,
    DateTimeOffset? UpdatedAt);

/// <summary>One fill of an order.</summary>
/// <param name="Commission">The fee, in <paramref name="CommissionAsset"/>.</param>
/// <param name="Maker">Whether the order was the maker for this fill.</param>
public sealed record ExchangeTrade(
    string Symbol,
    long TradeId,
    long OrderId,
    decimal Price,
    decimal Quantity,
    decimal Commission,
    string CommissionAsset,
    DateTimeOffset Time,
    bool Maker);

/// <param name="CanTrade">Spot only. Binance's Futures account doesn't say.</param>
/// <param name="Balances">Assets with a balance. Spot leaves out zero balances.</param>
/// <param name="TotalMarginBalance">Futures only.</param>
/// <param name="AvailableBalance">Futures only.</param>
/// <param name="Uid">Spot only: Binance's ID for the account, which tells whether two keys belong to one account.</param>
public sealed record ExchangeAccount(
    Market Market,
    bool? CanTrade,
    IReadOnlyList<AssetBalance> Balances,
    decimal? TotalMarginBalance,
    decimal? AvailableBalance,
    long? Uid = null);

/// <summary>A Live key's permissions, as Binance reports them (FR-KEY-05).</summary>
/// <param name="IpRestricted">Whether the key only works from the addresses listed for it.</param>
/// <param name="Reading">Whether it can read the account.</param>
/// <param name="Withdrawals">Whether it can withdraw funds, which Ecnanib never accepts (AC-04).</param>
/// <param name="SpotAndMarginTrading">Whether it can trade Spot.</param>
/// <param name="Futures">
/// Whether it can trade Futures. Binance says a key made before the Futures account was opened can't
/// use the Futures API, so check with a Futures request too.
/// </param>
public sealed record ApiRestrictions(bool IpRestricted, bool Reading, bool Withdrawals, bool SpotAndMarginTrading, bool Futures);

/// <param name="Total">Spot: free plus locked. Futures: the wallet balance.</param>
/// <param name="Available">Spot: free. Futures: available.</param>
public sealed record AssetBalance(string Asset, decimal Total, decimal Available);
