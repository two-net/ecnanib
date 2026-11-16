using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// How the clock of the service stands against the clocks of the exchange, which the gateway
/// reads on a schedule (FR-BIN-5). The exchange takes a signed call only when the time that the
/// call carries is near its own, so a clock that is off is one whose calls are refused. It is
/// what is asked before a new entry, and what an operator status screen shows (FR-OPS-2). See
/// docs/exchange.md.
/// </summary>
public interface IExchangeClock
{
    /// <summary>
    /// How the clock of the service stands against each clock of the exchange now, in a fixed
    /// order. Each market has a clock of its own in each environment, and each is held against
    /// on its own: one that is off leaves the others as they are.
    /// </summary>
    IReadOnlyList<ExchangeClockDrift> Drift();

    /// <summary>
    /// Whether new entries are paused on a market in an environment. They are unless the latest
    /// reading of its clock still counts and is within the tolerance, so a clock that was not
    /// read pauses them as one that is off does (NFR-REL-2). What is open stays as it is: its
    /// protective orders rest on the exchange.
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment.</param>
    bool EntriesPaused(Market market, TradingMode mode);
}

/// <summary>How the clock of the service stands against one clock of the exchange.</summary>
public enum ExchangeClockState
{
    /// <summary>
    /// There is no reading that counts: the clock of the exchange was not read yet, or not for
    /// longer than a reading counts.
    /// </summary>
    Unknown,

    /// <summary>The latest reading is within the tolerance. New entries go on.</summary>
    Within,

    /// <summary>The latest reading is further from the clock of the exchange than the tolerance.</summary>
    Exceeded,

    /// <summary>
    /// The exchange refused a call for the time that it carries, and its clock was not read
    /// since.
    /// </summary>
    Refused,
}

/// <summary>How the clock of the service stands against the clock of one market in one environment.</summary>
/// <param name="Market">The market.</param>
/// <param name="Environment">The environment.</param>
/// <param name="State">What the latest reading says, which is whether new entries are paused.</param>
/// <param name="Drift">
/// How far the clock of the service was from the clock of the exchange at the latest reading,
/// however old that is, or null when there is none. More than nothing is ahead of the exchange,
/// and less is behind it.
/// </param>
/// <param name="RoundTrip">
/// How long the exchange took to answer that reading. The drift is exact to half of it: the
/// exchange read its clock somewhere between the call and the answer.
/// </param>
/// <param name="ReadAt">When that reading was taken, by the clock of the service.</param>
/// <param name="Tolerance">How far the clock of the service may be from the exchange's, which the configuration file sets.</param>
public sealed record ExchangeClockDrift(
    Market Market,
    TradingMode Environment,
    ExchangeClockState State,
    TimeSpan? Drift,
    TimeSpan? RoundTrip,
    DateTimeOffset? ReadAt,
    TimeSpan Tolerance)
{
    /// <summary>Whether new entries are paused there: they are in every state but <see cref="ExchangeClockState.Within"/>.</summary>
    public bool EntriesPaused => State != ExchangeClockState.Within;
}
