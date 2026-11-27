using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// The watch on whether Binance answers (NFR-REL-4): it hears of every call that was sent, and
/// says that Binance is out on a market in an environment when as many calls in a row as the
/// configuration file says were left without an answer there. New entries pause while it is
/// out. See docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An answer is whatever Binance itself says:</b> a result, a refusal, an error of its own,
/// and its signal of too many requests, which the limiter sees to. No answer is a connection
/// that could not be made or broke off, a call that ran into its time limit, and a fault that
/// the address reports in Binance's place, which is every status from 500 on, and 408.
/// </para>
/// <para>
/// <b>It pauses no call.</b> The reads, the protective orders, and the closes go on being sent:
/// a position is protected and closed whenever that can be done, and each call that is made is
/// how the watch learns that Binance answers again. The first answer ends an outage. The clocks
/// of all four are read on a schedule, so a call is made there every minute at the least.
/// </para>
/// <para>
/// Each market has an address of its own in each environment, and is watched on its own, as
/// <see cref="BinanceClock"/> watches its clock. The call that asks what a key may do is not
/// counted: it is of Binance's wallet API, which says nothing about the orders of a market.
/// </para>
/// <para>
/// It keeps what it knows in memory, for this process, as the limiter does: a start begins
/// with no outage. Times are of the day of the application's clock.
/// </para>
/// </remarks>
internal sealed class BinanceOutages(BinanceSettings settings, TimeProvider clock, ILogger<BinanceOutages> logger) : IExchangeOutages
{
    private readonly Lock _gate = new();
    private readonly List<Watched> _watched = [.. BinanceClock.Clocks.Select(watched => new Watched(watched.Market, watched.Mode))];

    /// <summary>Takes that Binance answered a call on a market in an environment.</summary>
    public void Answered(Market market, TradingMode mode)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? since;

        lock (_gate)
        {
            var watched = Of(market, mode);

            since = watched.OutageSince;
            watched.Unanswered = 0;
            watched.OutageSince = null;
            watched.AnsweredAt = now;
        }

        // The end of an outage is said once, as its beginning is.
        if (since is { } began)
        {
            logger.LogInformation(
                "Binance answers again on {Market} {Environment}, after {OutageSeconds} s without an answer. New entries go on there",
                market.ToString(),
                mode.ToString(),
                Math.Round((now - began).TotalSeconds));
        }
    }

    /// <summary>Takes that a call on a market in an environment was sent, and that Binance left it without an answer.</summary>
    public void Unanswered(Market market, TradingMode mode)
    {
        var now = clock.GetUtcNow();
        int unanswered;

        lock (_gate)
        {
            var watched = Of(market, mode);

            unanswered = ++watched.Unanswered;

            if (watched.OutageSince is not null || unanswered < settings.OutageFailures)
            {
                return;
            }

            watched.OutageSince = now;
        }

        // Why each call had no answer is in the log of outbound calls. This is what follows from
        // them together, and is for whoever runs the service to see: an error.
        logger.LogError(
            "Binance left {Unanswered} calls in a row without an answer on {Market} {Environment}. New entries are paused there until it answers again",
            unanswered,
            market.ToString(),
            mode.ToString());
    }

    public IReadOnlyList<ExchangeAnswers> Answers()
    {
        lock (_gate)
        {
            return
            [
                .. _watched.Select(watched => new ExchangeAnswers(
                    watched.Market, watched.Mode, watched.Unanswered, watched.OutageSince, watched.AnsweredAt)),
            ];
        }
    }

    public bool EntriesPaused(Market market, TradingMode mode)
    {
        lock (_gate)
        {
            return Of(market, mode).OutageSince is not null;
        }
    }

    /// <summary>What is known of a market in an environment. Called with the gate held.</summary>
    private Watched Of(Market market, TradingMode mode)
    {
        foreach (var watched in _watched)
        {
            if (watched.Market == market && watched.Mode == mode)
            {
                return watched;
            }
        }

        throw new ArgumentException($"{market} is not watched in {mode}.");
    }

    /// <summary>The latest of one market in one environment.</summary>
    private sealed class Watched(Market market, TradingMode mode)
    {
        public Market Market { get; } = market;

        public TradingMode Mode { get; } = mode;

        /// <summary>The calls in a row that had no answer.</summary>
        public int Unanswered { get; set; }

        /// <summary>When the outage began that lasts now, or null when there is none.</summary>
        public DateTimeOffset? OutageSince { get; set; }

        /// <summary>When a call was last answered.</summary>
        public DateTimeOffset? AnsweredAt { get; set; }
    }
}
