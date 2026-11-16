using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// The watch on the clock: how the clock of the service stands against Binance's (FR-BIN-5).
/// Binance takes a signed call only when the time that the call carries is near its own, so new
/// entries pause while the clock of the service is further from Binance's than the configuration
/// file allows, and go on when it is back. It says what it found, for an operator status screen
/// (FR-OPS-2). See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// Each market has a clock in each environment, which makes four, and each is held against on
/// its own. <see cref="BinanceClockJob"/> reads them on a schedule and tells this what each
/// said. This keeps the latest reading of each, and nothing else.
/// </para>
/// <para>
/// A reading counts for <see cref="IntervalsOfAReading"/> intervals of the schedule, so a read
/// that fails once pauses nothing. After that the clock is unknown, and new entries pause until
/// it is read: a clock that cannot be read is not one that is right (NFR-REL-2). How old a
/// reading is, is counted in time that no change of the time of day moves, because the time of
/// day is what is in doubt here.
/// </para>
/// <para>
/// <see cref="BinanceCall"/> tells this when Binance refuses a call for the time that it
/// carries. That is Binance's own word that the clock is off, so it pauses new entries there
/// until the clock is read again.
/// </para>
/// <para>
/// It keeps what it knows in memory, for this process, as the limiter does.
/// </para>
/// </remarks>
internal sealed class BinanceClock(BinanceSettings settings, TimeProvider clock, ILogger<BinanceClock> logger) : IExchangeClock
{
    /// <summary>How many intervals of the schedule a reading counts for.</summary>
    public const int IntervalsOfAReading = 3;

    /// <summary>The clocks that are watched, in the order that they are reported in.</summary>
    public static readonly IReadOnlyList<(Market Market, TradingMode Mode)> Clocks =
    [
        (Market.Spot, TradingMode.Live),
        (Market.Spot, TradingMode.Demo),
        (Market.Futures, TradingMode.Live),
        (Market.Futures, TradingMode.Demo),
    ];

    private readonly Lock _gate = new();
    private readonly long _began = clock.GetTimestamp();
    private readonly TimeSpan _tolerance = TimeSpan.FromMilliseconds(settings.ClockToleranceMilliseconds);
    private readonly TimeSpan _counts = TimeSpan.FromSeconds((long)settings.ClockCheckSeconds * IntervalsOfAReading);
    private readonly List<Watched> _watched = [.. Clocks.Select(watched => new Watched(watched.Market, watched.Mode))];

    /// <summary>Takes a reading of a clock of Binance.</summary>
    /// <param name="market">The market whose clock was read.</param>
    /// <param name="mode">Its environment.</param>
    /// <param name="serverTime">What Binance's clock showed.</param>
    /// <param name="sentAt">When the call was sent, by the clock of the service.</param>
    /// <param name="took">How long the answer took.</param>
    public void Read(Market market, TradingMode mode, DateTimeOffset serverTime, DateTimeOffset sentAt, TimeSpan took)
    {
        // Binance read its clock somewhere between the call and the answer. The middle is the
        // nearest that can be told, and is right to half of the time between them.
        var at = sentAt + (took / 2);
        var drift = at - serverTime;
        var within = drift.Duration() <= _tolerance;
        ExchangeClockState? said;

        lock (_gate)
        {
            var watched = Of(market, mode);

            watched.Drift = drift;
            watched.RoundTrip = took;
            watched.ReadAt = at;
            watched.ReadAfter = Now();

            // A reading is newer than what Binance refused before it.
            watched.Refused = false;

            said = watched.Said;
            watched.Said = within ? null : ExchangeClockState.Exceeded;
        }

        var driftMs = (long)Math.Round(drift.TotalMilliseconds);

        logger.LogInformation(
            "The clock of the service differs from Binance's on {Market} {Environment} by {DriftMs} ms, read in {RoundTripMs} ms",
            market.ToString(),
            mode.ToString(),
            driftMs,
            Math.Round(took.TotalMilliseconds, 1));

        // A pause is said once, when it begins, and not with every reading while it lasts.
        if (!within && said != ExchangeClockState.Exceeded)
        {
            logger.LogWarning(
                "The clock of the service is {DriftMs} ms from Binance's on {Market} {Environment}, which is more than the {ToleranceMs} ms that it may be. New entries are paused there",
                driftMs,
                market.ToString(),
                mode.ToString(),
                settings.ClockToleranceMilliseconds);
        }
        else if (within && said is not null)
        {
            logger.LogInformation(
                "The clock of the service is within {ToleranceMs} ms of Binance's on {Market} {Environment} again. New entries go on there",
                settings.ClockToleranceMilliseconds,
                market.ToString(),
                mode.ToString());
        }
    }

    /// <summary>
    /// Takes that a clock of Binance could not be read. The reading before it stands for as long
    /// as it counts.
    /// </summary>
    /// <param name="market">The market whose clock was asked.</param>
    /// <param name="mode">Its environment.</param>
    public void Unread(Market market, TradingMode mode)
    {
        lock (_gate)
        {
            var watched = Of(market, mode);

            if (StateOf(watched, Now()) != ExchangeClockState.Unknown || watched.Said == ExchangeClockState.Unknown)
            {
                return;
            }

            watched.Said = ExchangeClockState.Unknown;
        }

        // Why it could not be read is in the log already: the call says how it ended.
        logger.LogWarning(
            "The clock of Binance on {Market} {Environment} could not be read, and there is no reading of it that still counts. New entries are paused there until it is read",
            market.ToString(),
            mode.ToString());
    }

    /// <summary>
    /// Takes that Binance refused a call for the time that the call carries, which is its error
    /// for a time outside of what it allows.
    /// </summary>
    /// <param name="market">The market of the call.</param>
    /// <param name="mode">Its environment.</param>
    public void Refused(Market market, TradingMode mode)
    {
        lock (_gate)
        {
            var watched = Of(market, mode);
            watched.Refused = true;

            if (watched.Said == ExchangeClockState.Refused)
            {
                return;
            }

            watched.Said = ExchangeClockState.Refused;
        }

        logger.LogWarning(
            "Binance refused a call on {Market} {Environment} for the time that it carries. New entries are paused there until its clock is read again",
            market.ToString(),
            mode.ToString());
    }

    public IReadOnlyList<ExchangeClockDrift> Drift()
    {
        lock (_gate)
        {
            var now = Now();

            return
            [
                .. _watched.Select(watched => new ExchangeClockDrift(
                    watched.Market,
                    watched.Mode,
                    StateOf(watched, now),
                    watched.Drift,
                    watched.RoundTrip,
                    watched.ReadAt,
                    _tolerance)),
            ];
        }
    }

    public bool EntriesPaused(Market market, TradingMode mode)
    {
        lock (_gate)
        {
            return StateOf(Of(market, mode), Now()) != ExchangeClockState.Within;
        }
    }

    /// <summary>What the latest reading of a clock says now. Called with the gate held.</summary>
    private ExchangeClockState StateOf(Watched watched, TimeSpan now)
    {
        if (watched.Refused)
        {
            return ExchangeClockState.Refused;
        }

        if (watched.Drift is not { } drift || now - watched.ReadAfter > _counts)
        {
            return ExchangeClockState.Unknown;
        }

        return drift.Duration() <= _tolerance ? ExchangeClockState.Within : ExchangeClockState.Exceeded;
    }

    /// <summary>The time since the watch began, which no change of the time of day moves.</summary>
    private TimeSpan Now() => clock.GetElapsedTime(_began);

    /// <summary>What is known of the clock of a market in an environment. Called with the gate held.</summary>
    private Watched Of(Market market, TradingMode mode)
    {
        foreach (var watched in _watched)
        {
            if (watched.Market == market && watched.Mode == mode)
            {
                return watched;
            }
        }

        throw new ArgumentException($"{market} has no clock in {mode}.");
    }

    /// <summary>The latest of one clock of Binance.</summary>
    private sealed class Watched(Market market, TradingMode mode)
    {
        public Market Market { get; } = market;

        public TradingMode Mode { get; } = mode;

        /// <summary>How far the clock of the service was from this one at the latest reading, or null when it was not read.</summary>
        public TimeSpan? Drift { get; set; }

        public TimeSpan? RoundTrip { get; set; }

        /// <summary>When the latest reading was taken, by the clock of the service.</summary>
        public DateTimeOffset? ReadAt { get; set; }

        /// <summary>The same in the time since the watch began, by which a reading grows old.</summary>
        public TimeSpan ReadAfter { get; set; }

        /// <summary>Whether Binance refused a call for its time since the latest reading.</summary>
        public bool Refused { get; set; }

        /// <summary>The pause that the log was told of, or null when it was told of none, or that it is over.</summary>
        public ExchangeClockState? Said { get; set; }
    }
}
