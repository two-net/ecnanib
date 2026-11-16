using System.Net;
using System.Reflection;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// The clock of the service stays within what Binance takes a signed call with: how far it is
/// from each of Binance's clocks is measured on a schedule and logged, new entries pause while it
/// is further than the configuration file allows, and go on when it is back (FR-BIN-5). A clock
/// that was not read pauses them as one that is off does (NFR-REL-2). What was found is reported,
/// for an operator status screen (FR-OPS-2).
/// </summary>
public class ClockWatchTests
{
    private const string OutsideWindow = """{ "code": -1021, "msg": "Timestamp for this request is outside of the recvWindow." }""";

    // What the configuration file of the tests allows: see SettingsJson.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    private static readonly DateTimeOffset Start = BinanceCalls.Now;

    [Fact]
    public async Task Each_clock_of_binance_is_read_as_the_work_starts_and_what_it_differs_by_is_logged_and_reported()
    {
        using var watch = new Watch();
        watch.Of(Market.Spot, TradingMode.Live).Skew = TimeSpan.FromMilliseconds(120);
        watch.Of(Market.Spot, TradingMode.Demo).Skew = TimeSpan.FromMilliseconds(-80);
        watch.Of(Market.Futures, TradingMode.Demo).Skew = TimeSpan.FromMilliseconds(35);

        await watch.StartedAsync();

        // The clock of each market in each environment, and nothing else. A read is the
        // service's own call: it carries no key and no time of its own.
        Assert.Equal(
            [
                "GET https://futures-demo.example/fapi/v1/time",
                "GET https://futures-live.example/fapi/v1/time",
                "GET https://spot-demo.example/api/v3/time",
                "GET https://spot-live.example/api/v3/time",
            ],
            watch.Binance.Calls.Select(call => $"{call.Method} {call.Address}").Order(StringComparer.Ordinal));
        Assert.All(watch.Binance.Calls, call => Assert.False(call.Headers.ContainsKey("X-MBX-APIKEY")));

        // It costs the least that a call can, of the limit of its market in its environment.
        var limits = watch.Application.Services.GetRequiredService<IExchangeLimits>().Usage();
        Assert.Equal([1, 1, 1, 1], limits.Where(limit => limit.Name is "Spot" or "Futures").Select(limit => limit.Used));

        // Each reading is in the log, with how far the clock of the service is ahead.
        var readings = watch.Said().Where(said => said.Template == Watch.Reading).ToList();
        Assert.All(readings, said => Assert.Equal("Information", said.Level));
        Assert.Equal(
            [("Futures", "Demo", 35L), ("Futures", "Live", 0L), ("Spot", "Demo", -80L), ("Spot", "Live", 120L)],
            readings.Select(said => (said.Text("Market")!, said.Text("Environment")!, said["DriftMs"].GetInt64())).Order());
        Assert.All(readings, said => Assert.Equal(0, said["RoundTripMs"].GetDouble()));

        // Nothing is off, so that is all that is said.
        Assert.Equal(4, watch.Said().Count);

        // What an operator status screen shows: each clock, in a fixed order.
        Assert.Equal(
            [
                Within(Market.Spot, TradingMode.Live, 120),
                Within(Market.Spot, TradingMode.Demo, -80),
                Within(Market.Futures, TradingMode.Live, 0),
                Within(Market.Futures, TradingMode.Demo, 35),
            ],
            watch.ExchangeClock.Drift());
        Assert.All(watch.ExchangeClock.Drift(), clock => Assert.False(clock.EntriesPaused));
        Assert.All(BinanceCalls.Pairs, pair => Assert.False(watch.ExchangeClock.EntriesPaused(pair.Market, pair.Mode)));

        static ExchangeClockDrift Within(Market market, TradingMode mode, int ahead) =>
            new(market, mode, ExchangeClockState.Within, TimeSpan.FromMilliseconds(ahead), TimeSpan.Zero, Start, Tolerance);
    }

    [Fact]
    public async Task The_clocks_are_read_again_when_the_time_between_two_readings_has_passed()
    {
        using var watch = await new Watch().StartedAsync();
        Assert.Equal(4, watch.Binance.Calls.Count);

        await watch.NextRunAsync();

        Assert.Equal(8, watch.Binance.Calls.Count);
        Assert.All(watch.ExchangeClock.Drift(), clock => Assert.Equal(Start + Interval + TimeSpan.FromSeconds(1), clock.ReadAt));
    }

    [Fact]
    public async Task The_clocks_are_read_as_the_work_starts_although_the_time_between_two_readings_has_not_passed()
    {
        var database = PostgresServer.CreateDatabase();

        using (var before = await new Watch(database).StartedAsync())
        {
            Assert.Equal(4, before.Binance.Calls.Count);
        }

        // What was read is kept in memory, so an application that starts knows nothing of the
        // clocks, however lately the one before it read them.
        using var watch = await new Watch(database, Start.AddSeconds(10)).StartedAsync(since: Start.AddSeconds(10));

        Assert.Equal(4, watch.Binance.Calls.Count);
        Assert.All(watch.ExchangeClock.Drift(), clock => Assert.Equal(ExchangeClockState.Within, clock.State));
    }

    [Fact]
    public async Task What_the_clock_differs_by_is_taken_at_the_middle_of_the_call()
    {
        using var watch = new Watch();

        // Binance takes 300 ms to answer, and reads its clock half way. The clock of the service
        // is a quarter of a second ahead of it then.
        var side = watch.Of(Market.Futures, TradingMode.Live);
        side.Trip = TimeSpan.FromMilliseconds(300);
        side.Skew = TimeSpan.FromMilliseconds(250);

        await watch.StartedAsync();

        var reported = watch.Reported(Market.Futures, TradingMode.Live);
        Assert.Equal(TimeSpan.FromMilliseconds(250), reported.Drift);
        Assert.Equal(TimeSpan.FromMilliseconds(300), reported.RoundTrip);
        Assert.Equal(Start.AddMilliseconds(150), reported.ReadAt);
        Assert.Equal(ExchangeClockState.Within, reported.State);

        var said = Assert.Single(watch.Said(), said => said.Template == Watch.Reading && said.Text("Market") == "Futures" && said.Text("Environment") == "Live");
        Assert.Equal(250, said["DriftMs"].GetInt64());
        Assert.Equal(300, said["RoundTripMs"].GetDouble());
    }

    [Fact]
    public async Task The_wait_for_a_turn_at_the_limiter_is_no_part_of_a_reading()
    {
        using var watch = new Watch();
        var side = watch.Of(Market.Spot, TradingMode.Live);
        side.Skew = TimeSpan.FromMilliseconds(90);

        // Binance says that it has had too many requests on Spot, for a second.
        side.Instead = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { { "Retry-After", "1" } } };
        await watch.StartedAsync();
        side.Instead = null;

        // The next read of that clock waits for its turn, and five seconds pass before it is sent.
        var run = watch.RunAsync();
        var limits = watch.Application.Services.GetRequiredService<IExchangeLimits>();
        await Wait.UntilAsync(
            () => limits.Usage().Single(limit => limit.Name == "Spot" && limit.Environment == TradingMode.Live).Waiting == 1,
            "the read of the clock of Spot to wait for its turn");
        watch.Clock.Advance(TimeSpan.FromSeconds(5));
        await run;

        // The reading is of the call alone: it is taken from when the call was sent.
        var reported = watch.Reported(Market.Spot, TradingMode.Live);
        Assert.Equal(TimeSpan.FromMilliseconds(90), reported.Drift);
        Assert.Equal(TimeSpan.Zero, reported.RoundTrip);
        Assert.Equal(Start.AddSeconds(5), reported.ReadAt);
        Assert.Equal(ExchangeClockState.Within, reported.State);
    }

    [Theory]

    // Ahead of Binance, and behind it.
    [InlineData(1)]
    [InlineData(-1)]
    public async Task New_entries_pause_while_the_clock_is_further_from_binances_than_the_tolerance_and_go_on_when_it_is_back(int direction)
    {
        using var watch = new Watch();
        var side = watch.Of(Market.Spot, TradingMode.Demo);
        side.Skew = direction * (Tolerance + TimeSpan.FromMilliseconds(1));

        await watch.StartedAsync();

        Assert.True(watch.ExchangeClock.EntriesPaused(Market.Spot, TradingMode.Demo));
        var reported = watch.Reported(Market.Spot, TradingMode.Demo);
        Assert.Equal(ExchangeClockState.Exceeded, reported.State);
        Assert.True(reported.EntriesPaused);
        Assert.Equal(side.Skew, reported.Drift);

        // It is said, as a warning, with how far the clock is off and how far it may be.
        var paused = Assert.Single(watch.Said(), said => said.Template == Watch.Exceeded);
        Assert.Equal("Warning", paused.Level);
        Assert.Equal("Spot", paused.Text("Market"));
        Assert.Equal("Demo", paused.Text("Environment"));
        Assert.Equal(direction * 401, paused["DriftMs"].GetInt64());
        Assert.Equal(400, paused["ToleranceMs"].GetInt32());

        // Exactly as far as it may be is within it.
        side.Skew = direction * Tolerance;
        await watch.RunAsync();

        Assert.False(watch.ExchangeClock.EntriesPaused(Market.Spot, TradingMode.Demo));
        Assert.Equal(ExchangeClockState.Within, watch.Reported(Market.Spot, TradingMode.Demo).State);

        var resumed = Assert.Single(watch.Said(), said => said.Template == Watch.WithinAgain);
        Assert.Equal("Information", resumed.Level);
        Assert.Equal("Spot", resumed.Text("Market"));
        Assert.Equal("Demo", resumed.Text("Environment"));
        Assert.Equal(400, resumed["ToleranceMs"].GetInt32());
    }

    [Fact]
    public async Task A_clock_that_is_off_pauses_new_entries_on_its_own_market_and_environment_and_on_no_other()
    {
        using var watch = new Watch();
        watch.Of(Market.Futures, TradingMode.Live).Skew = TimeSpan.FromSeconds(2);

        await watch.StartedAsync();

        Assert.Equal(
            [(Market.Futures, TradingMode.Live)],
            BinanceCalls.Pairs.Where(pair => watch.ExchangeClock.EntriesPaused(pair.Market, pair.Mode)));
        Assert.Equal(
            [ExchangeClockState.Within, ExchangeClockState.Within, ExchangeClockState.Exceeded, ExchangeClockState.Within],
            watch.ExchangeClock.Drift().Select(clock => clock.State));
    }

    [Fact]
    public async Task A_pause_and_its_end_are_each_said_once_and_not_with_every_reading()
    {
        using var watch = new Watch();
        var side = watch.Of(Market.Spot, TradingMode.Live);
        side.Skew = TimeSpan.FromSeconds(-3);

        await watch.StartedAsync();
        await watch.RunAsync();
        await watch.RunAsync();

        // Every reading is in the log, and the pause once.
        Assert.Equal(12, watch.Said().Count(said => said.Template == Watch.Reading));
        Assert.Single(watch.Said(), said => said.Template == Watch.Exceeded);

        side.Skew = TimeSpan.Zero;
        await watch.RunAsync();
        await watch.RunAsync();

        Assert.Single(watch.Said(), said => said.Template == Watch.WithinAgain);
        Assert.Single(watch.Said(), said => said.Level == "Warning");
    }

    [Fact]
    public void Until_a_clock_was_read_new_entries_are_paused()
    {
        // An application that does not read the clocks: that of a test which is not about them.
        using var application = BinanceCalls.Behind(BinanceCalls.ThatAnswers());
        var clock = application.Services.GetRequiredService<IExchangeClock>();

        Assert.Equal(
            [.. BinanceCalls.Pairs.Select(pair => new ExchangeClockDrift(
                pair.Market, pair.Mode, ExchangeClockState.Unknown, Drift: null, RoundTrip: null, ReadAt: null, Tolerance))],
            clock.Drift());
        Assert.All(clock.Drift(), reported => Assert.True(reported.EntriesPaused));
        Assert.All(BinanceCalls.Pairs, pair => Assert.True(clock.EntriesPaused(pair.Market, pair.Mode)));
    }

    [Fact]
    public async Task A_clock_that_cannot_be_read_pauses_new_entries_until_it_is_read()
    {
        using var watch = new Watch();
        var side = watch.Of(Market.Futures, TradingMode.Demo);
        side.Instead = () => throw new HttpRequestException("The connection was refused.");

        await watch.StartedAsync();
        await watch.RunAsync();

        Assert.Equal(
            [(Market.Futures, TradingMode.Demo)],
            BinanceCalls.Pairs.Where(pair => watch.ExchangeClock.EntriesPaused(pair.Market, pair.Mode)));
        Assert.Equal(
            new ExchangeClockDrift(Market.Futures, TradingMode.Demo, ExchangeClockState.Unknown, null, null, null, Tolerance),
            watch.Reported(Market.Futures, TradingMode.Demo));

        // Said once, as a warning, however often the read fails.
        var unread = Assert.Single(watch.Said(), said => said.Template == Watch.Unread);
        Assert.Equal("Warning", unread.Level);
        Assert.Equal("Futures", unread.Text("Market"));
        Assert.Equal("Demo", unread.Text("Environment"));

        side.Instead = null;
        await watch.RunAsync();

        Assert.False(watch.ExchangeClock.EntriesPaused(Market.Futures, TradingMode.Demo));
        Assert.Single(watch.Said(), said => said.Template == Watch.WithinAgain);
    }

    [Fact]
    public async Task A_reading_counts_for_three_times_the_time_between_two_readings_and_then_the_clock_is_unknown()
    {
        using var watch = await new Watch().StartedAsync();
        var side = watch.Of(Market.Spot, TradingMode.Live);
        side.Instead = () => StandInSystem.Status(HttpStatusCode.ServiceUnavailable);

        // Two reads in a row that fail pause nothing: the reading before them still counts.
        await watch.NextRunAsync();
        await watch.NextRunAsync();

        Assert.False(watch.ExchangeClock.EntriesPaused(Market.Spot, TradingMode.Live));
        Assert.Equal(ExchangeClockState.Within, watch.Reported(Market.Spot, TradingMode.Live).State);
        Assert.DoesNotContain(watch.Said(), said => said.Level == "Warning");

        // With the third, it is older than that.
        await watch.NextRunAsync();

        Assert.True(watch.ExchangeClock.EntriesPaused(Market.Spot, TradingMode.Live));
        Assert.Equal(
            new ExchangeClockDrift(Market.Spot, TradingMode.Live, ExchangeClockState.Unknown, TimeSpan.Zero, TimeSpan.Zero, Start, Tolerance),
            watch.Reported(Market.Spot, TradingMode.Live));
        Assert.Single(watch.Said(), said => said.Template == Watch.Unread);

        // The other clocks were read all along.
        Assert.Equal(
            [(Market.Spot, TradingMode.Live)],
            BinanceCalls.Pairs.Where(pair => watch.ExchangeClock.EntriesPaused(pair.Market, pair.Mode)));

        side.Instead = null;
        await watch.RunAsync();

        Assert.False(watch.ExchangeClock.EntriesPaused(Market.Spot, TradingMode.Live));
        Assert.Single(watch.Said(), said => said.Template == Watch.WithinAgain);
    }

    [Fact]
    public async Task A_call_that_binance_refuses_for_its_time_pauses_new_entries_there_until_the_clock_is_read_again()
    {
        using var watch = await new Watch().StartedAsync();
        var exchange = watch.Application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);
        watch.Of(Market.Futures, TradingMode.Demo).RefusesTheTime = true;

        // The call ends as any other failure does: it says nothing about the credential.
        for (var times = 0; times < 2; times++)
        {
            var balances = await exchange.GetBalancesAsync(
                BinanceCalls.User, Market.Futures, TradingMode.Demo, apiKey, secretKey, TestContext.Current.CancellationToken);
            Assert.Equal(ExchangeCallOutcome.Failed, balances.Outcome);
        }

        // Binance's own word that the clock is off counts for more than the reading before it.
        Assert.Equal(
            [(Market.Futures, TradingMode.Demo)],
            BinanceCalls.Pairs.Where(pair => watch.ExchangeClock.EntriesPaused(pair.Market, pair.Mode)));
        Assert.Equal(
            new ExchangeClockDrift(Market.Futures, TradingMode.Demo, ExchangeClockState.Refused, TimeSpan.Zero, TimeSpan.Zero, Start, Tolerance),
            watch.Reported(Market.Futures, TradingMode.Demo));

        // Said once, under the watch on the clock. The gateway says which error it was with each call.
        var refused = Assert.Single(watch.Said(), said => said.Template == Watch.Refused);
        Assert.Equal("Warning", refused.Level);
        Assert.Equal("Futures", refused.Text("Market"));
        Assert.Equal("Demo", refused.Text("Environment"));
        Assert.Equal(2, watch.Application.Events().Count(said => said.Area == BinanceCalls.GatewayArea && said.Has("BinanceCode")));

        // The next reading is newer than the refusal, and is within the tolerance.
        await watch.RunAsync();

        Assert.False(watch.ExchangeClock.EntriesPaused(Market.Futures, TradingMode.Demo));
        Assert.Equal(ExchangeClockState.Within, watch.Reported(Market.Futures, TradingMode.Demo).State);
        Assert.Single(watch.Said(), said => said.Template == Watch.WithinAgain);
    }

    [Fact]
    public void Once_the_gateway_places_orders_what_places_an_entry_asks_whether_entries_are_paused()
    {
        // Orders are not built yet, and nothing opens a position. As soon as the gateway has its
        // namespace for them, what places an entry must ask: the watch pauses nothing by itself.
        var types = typeof(IExchangeClock).Assembly.GetTypes();
        var placesOrders = types.Any(type => IsOf(type, "Ecnanib.ExchangeGateway.Trading"));
        var asks = types
            .Where(type => IsOf(type, "Ecnanib.ExchangeGateway.Trading") || IsOf(type, "Ecnanib.DecisionEngine") || IsOf(type, "Ecnanib.Agents"))
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(IExchangeClock));

        Assert.True(
            asks || !placesOrders,
            "The gateway places orders, and nothing asks whether new entries are paused. They are while the clock of the " +
            "service is further from Binance's than the tolerance, or was not read (FR-BIN-5). Take " +
            $"{nameof(IExchangeClock)} where an entry is placed, in Ecnanib.ExchangeGateway.Trading or in what calls it, and " +
            $"place none while {nameof(IExchangeClock.EntriesPaused)} says so for the market and the environment of the agent. " +
            "See docs/exchange.md.");

        static bool IsOf(Type type, string area) =>
            type.Namespace is { } name && (name == area || name.StartsWith($"{area}.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The application that reads Binance's clocks on its schedule, on a database of its own,
    /// behind a Binance whose clocks the test sets, on a clock that only the test moves.
    /// </summary>
    private sealed class Watch : IDisposable
    {
        public const string Job = "clock-watch";

        public const string Area = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceClock";

        public const string Reading =
            "The clock of the service differs from Binance's on {Market} {Environment} by {DriftMs} ms, read in {RoundTripMs} ms";

        public const string Exceeded =
            "The clock of the service is {DriftMs} ms from Binance's on {Market} {Environment}, which is more than the {ToleranceMs} ms that it may be. New entries are paused there";

        public const string WithinAgain =
            "The clock of the service is within {ToleranceMs} ms of Binance's on {Market} {Environment} again. New entries go on there";

        public const string Unread =
            "The clock of Binance on {Market} {Environment} could not be read, and there is no reading of it that still counts. New entries are paused there until it is read";

        public const string Refused =
            "Binance refused a call on {Market} {Environment} for the time that it carries. New entries are paused there until its clock is read again";

        private readonly Lock _gate = new();
        private readonly Dictionary<(Market, TradingMode), Side> _sides = BinanceCalls.Pairs.ToDictionary(pair => pair, _ => new Side());

        /// <param name="database">The database, when the test starts more than one application on it.</param>
        /// <param name="start">What the clock of the application shows as it starts, when that is not <see cref="Start"/>.</param>
        public Watch(TestDatabase? database = null, DateTimeOffset? start = null)
        {
            Clock = new MovedClock(start ?? Start);
            Database = database ?? PostgresServer.CreateDatabase();
            Binance = new StandInSystem(Answer);
            Application = BinanceCalls.Behind(Binance, clock: Clock, database: Database, watchesClock: true);
        }

        public MovedClock Clock { get; }

        public TestDatabase Database { get; }

        public StandInSystem Binance { get; }

        public LoggedApplication Application { get; }

        public IExchangeClock ExchangeClock => Application.Services.GetRequiredService<IExchangeClock>();

        /// <summary>What Binance's side of one clock does.</summary>
        public Side Of(Market market, TradingMode mode) => _sides[(market, mode)];

        public ExchangeClockDrift Reported(Market market, TradingMode mode) =>
            ExchangeClock.Drift().Single(clock => clock.Market == market && clock.Environment == mode);

        /// <summary>What the watch on the clock wrote to the log.</summary>
        public IReadOnlyList<LoggedEvent> Said() => [.. Application.Events().Where(written => written.Area == Area)];

        /// <summary>
        /// Starts the application, and waits until the clocks have had their first reading on
        /// this database, which is due as the background work starts there. The catalog has its
        /// first sync then as well, and that is waited for too, so that nothing else writes to
        /// the log while a test reads it.
        /// </summary>
        /// <param name="since">What the clock shows as the application starts, when another application ran on the database before.</param>
        public async Task<Watch> StartedAsync(DateTimeOffset? since = null)
        {
            _ = Application.Services;
            await Wait.UntilAsync(
                () => Ran(Job, since ?? DateTimeOffset.MinValue) && Ran("catalog-sync", DateTimeOffset.MinValue),
                "the first reading of Binance's clocks");

            return this;
        }

        /// <summary>
        /// Reads the clocks now, as the scheduler does when the reading is due. A test does it
        /// itself to see what a reading does at a moment of its choosing.
        /// </summary>
        public Task RunAsync() =>
            Application.Services.GetServices<IBackgroundJob>()
                .Single(job => job.Name == Job)
                .RunAsync(new BackgroundJobRun(Clock.GetUtcNow()), TestContext.Current.CancellationToken);

        /// <summary>Lets the time between two readings pass, and waits for the reading that the scheduler then starts.</summary>
        public async Task NextRunAsync()
        {
            Clock.Advance(Interval + TimeSpan.FromSeconds(1));
            var due = Clock.GetUtcNow();

            await Wait.UntilAsync(() => Ran(Job, due), "the next reading of Binance's clocks");
        }

        public void Dispose() => Application.Dispose();

        /// <summary>Whether a job has finished a run that started at a time or after it.</summary>
        private bool Ran(string job, DateTimeOffset since) =>
            StoredJob.Of(Database, job) is { Outcome: not null } run && run.StartedAt >= since;

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var address = request.RequestUri!;
            var side = _sides[BinanceCalls.Pairs.Single(pair => BinanceCalls.HostOf(pair.Market, pair.Mode) == address.Host)];

            if (address.AbsolutePath is not ("/api/v3/time" or "/fapi/v1/time"))
            {
                return side.RefusesTheTime
                    ? StandInSystem.Json(HttpStatusCode.BadRequest, OutsideWindow)
                    : StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(address.AbsolutePath));
            }

            if (side.Instead is { } instead)
            {
                return instead();
            }

            lock (_gate)
            {
                // The answer takes as long as the trip, and Binance reads its clock half way.
                var arrival = Clock.GetUtcNow();
                Clock.Advance(side.Trip);
                var serverTime = arrival + (side.Trip / 2) - side.Skew;

                return StandInSystem.Json(HttpStatusCode.OK, $$"""{ "serverTime": {{serverTime.ToUnixTimeMilliseconds()}} }""");
            }
        }
    }

    /// <summary>What Binance's side of the clock of one market in one environment does.</summary>
    private sealed class Side
    {
        /// <summary>How far the clock of the service is ahead of this one. Less than nothing is behind it.</summary>
        public TimeSpan Skew { get; set; }

        /// <summary>How long Binance takes to answer a read of this clock.</summary>
        public TimeSpan Trip { get; set; }

        /// <summary>What a read of this clock is answered with in place of the time, when it is. It may fail, as a connection does.</summary>
        public Func<HttpResponseMessage>? Instead { get; set; }

        /// <summary>Whether a call with a credential is answered with Binance's error for a time outside of what it allows.</summary>
        public bool RefusesTheTime { get; set; }
    }

    /// <summary>
    /// A clock that stands still until a test moves it, in the time of day and in the time that
    /// has passed alike, so that a reading is exact. Waiting takes real time, so the scheduler
    /// notices a moved clock within one of its beats. <see cref="TestClock"/> is not this: there
    /// time passes by itself, which the limiter goes by in its own tests.
    /// </summary>
    private sealed class MovedClock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        private long _passed;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override long GetTimestamp() => Interlocked.Read(ref _passed);

        /// <summary>Lets time pass.</summary>
        public void Advance(TimeSpan by)
        {
            Interlocked.Add(ref _passed, by.Ticks);
            Interlocked.Add(ref _ticks, by.Ticks);
        }
    }
}
