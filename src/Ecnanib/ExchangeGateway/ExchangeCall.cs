namespace Ecnanib.ExchangeGateway;

/// <summary>
/// How a call to the exchange ended. A call that gives no answer is not an error of the gateway:
/// it is one of these, so that a caller, which may name no type of the network, can tell what to
/// do next. See docs/exchange.md.
/// </summary>
public enum ExchangeCallOutcome
{
    /// <summary>The exchange answered with what was asked for.</summary>
    Succeeded,

    /// <summary>
    /// The exchange answered as it answers a wrong credential. It says that the credential should
    /// be checked again, and not that it is wrong: the check of the key decides that (FR-KEY-3).
    /// </summary>
    AuthenticationFailed,

    /// <summary>The call ended without an answer, and its caller did not cancel it: the exchange took longer than the limit.</summary>
    TimedOut,

    /// <summary>
    /// Anything else: no connection, a limit on requests, a clock that is off, a block of the
    /// service's address, a fault of the exchange, or an answer that is not what the exchange
    /// sends.
    /// </summary>
    Failed,
}

/// <summary>What reading the exchange's clock gave.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="ServerTime">The time on the exchange's clock. It is there when the call succeeded, and never otherwise.</param>
public sealed record ExchangeTime(ExchangeCallOutcome Outcome, DateTimeOffset? ServerTime)
{
    public DateTimeOffset? ServerTime { get; } = (Outcome == ExchangeCallOutcome.Succeeded) == (ServerTime is not null)
        ? ServerTime
        : throw new ArgumentException("A reading has a time when it succeeded, and only then.", nameof(ServerTime));
}

/// <summary>What reading the balances of an account gave.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Balances">
/// What the account holds on the market, each asset once, when the call succeeded, and nothing
/// otherwise. An asset that is not among them is one the account holds none of. A list is whole
/// or it is not a list: a part of one would make the rest look empty.
/// </param>
public sealed record ExchangeBalances(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeBalance> Balances)
{
    public IReadOnlyList<ExchangeBalance> Balances { get; } = Outcome == ExchangeCallOutcome.Succeeded || Balances.Count == 0
        ? Balances
        : throw new ArgumentException("A reading has balances only when it succeeded.", nameof(Balances));
}

/// <summary>What an account holds of one asset on one market.</summary>
/// <param name="Asset">The exchange's name of the asset, such as <c>USDT</c>.</param>
/// <param name="Total">All of it: what is free, and what open orders or positions hold.</param>
/// <param name="Available">The part that a new order can use.</param>
public sealed record ExchangeBalance(string Asset, decimal Total, decimal Available);
