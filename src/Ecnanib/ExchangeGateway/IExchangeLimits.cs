using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// What the service has used of the exchange's limits on requests, as the one limiter of the
/// gateway counts it (FR-BIN-4). It is what an operator status screen shows (FR-OPS-2). See
/// docs/exchange.md.
/// </summary>
public interface IExchangeLimits
{
    /// <summary>
    /// The usage of each limit now, in a fixed order. The exchange counts each of them against
    /// the address of the service, whoever the calls are for, and each on its own: a limit that
    /// is used up leaves the others as they are.
    /// </summary>
    IReadOnlyList<ExchangeLimitUsage> Usage();

    /// <summary>
    /// Whether a read that nothing depends on may be made now on a market in an environment:
    /// one that only keeps a price on a page up to date (FR-NTF-2). It may while less than half
    /// of what the service allows itself of the limit of that market is used, no call waits for
    /// its turn there, and the exchange has not signalled that it has had too many. So what
    /// places and protects orders keeps its room, whatever the pages ask for.
    /// </summary>
    /// <param name="market">The market whose limit the read counts against.</param>
    /// <param name="environment">The environment, which has limits of its own.</param>
    bool HasRoom(Market market, TradingMode environment);
}

/// <summary>What the service has used of one limit on requests of the exchange.</summary>
/// <param name="Name">What the limit is on, such as <c>Spot</c>.</param>
/// <param name="Environment">The environment whose calls it counts.</param>
/// <param name="Limit">The request weight that the exchange allows the address of the service in a minute.</param>
/// <param name="Allowed">The part of <paramref name="Limit"/> that the service allows itself, which the configuration file sets.</param>
/// <param name="Used">The request weight of the calls that the limiter let through in the last minute.</param>
/// <param name="Reported">
/// What the exchange itself had counted when it last said so, with an answer, or null when it
/// has not said. It counts by its own minute, so it is not <paramref name="Used"/> to the digit.
/// </param>
/// <param name="ReportedAt">When the exchange said it.</param>
/// <param name="Waiting">The calls that wait for their turn.</param>
/// <param name="HeldUntil">
/// Until when no call is sent, because the exchange signalled that it has had too many, or null
/// when calls are sent.
/// </param>
public sealed record ExchangeLimitUsage(
    string Name,
    TradingMode Environment,
    int Limit,
    int Allowed,
    int Used,
    int? Reported,
    DateTimeOffset? ReportedAt,
    int Waiting,
    DateTimeOffset? HeldUntil);
