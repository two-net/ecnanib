using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// Whether the exchange answers the calls of the service, on each market in each environment
/// (NFR-REL-4). An exchange that leaves several calls in a row without an answer is out there:
/// new entries pause until it answers again, and the users whose agents run there are told. It
/// is what is asked before a new entry, what tells the users, and what an operator status screen
/// shows. See docs/reliability.md.
/// </summary>
public interface IExchangeOutages
{
    /// <summary>
    /// How the exchange answers now on each market in each environment, in a fixed order. Each
    /// has an address of its own, and is counted on its own: one that is out leaves the others
    /// as they are.
    /// </summary>
    IReadOnlyList<ExchangeAnswers> Answers();

    /// <summary>
    /// Whether new entries are paused on a market in an environment because the exchange is out
    /// there. Nothing else is paused: the calls go on being made, which is how the service
    /// learns that the exchange answers again, and what is open stays as it is, with its
    /// protective orders resting on the exchange (NFR-REL-2).
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment.</param>
    bool EntriesPaused(Market market, TradingMode mode);
}

/// <summary>How the exchange answers the calls of the service on one market in one environment.</summary>
/// <param name="Market">The market.</param>
/// <param name="Environment">The environment.</param>
/// <param name="Unanswered">
/// How many calls in a row the exchange has left without an answer there: none when its latest
/// call was answered. A refusal is an answer, and so is the signal of too many requests.
/// </param>
/// <param name="OutageSince">
/// When the outage began that lasts now, by the clock of the service, or null when there is
/// none: the moment of the call without an answer that was one too many.
/// </param>
/// <param name="AnsweredAt">When the exchange last answered a call there, or null when it has answered none since the start.</param>
public sealed record ExchangeAnswers(
    Market Market, TradingMode Environment, int Unanswered, DateTimeOffset? OutageSince, DateTimeOffset? AnsweredAt)
{
    /// <summary>Whether new entries are paused there: they are while an outage lasts.</summary>
    public bool EntriesPaused => OutageSince is not null;
}
