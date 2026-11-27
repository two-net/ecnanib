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
/// A Binance outage pauses entries (NFR-REL-4). The gateway hears of every call that it sends:
/// several in a row that Binance leaves without an answer, on a market in an environment, are an
/// outage there, and new entries pause until Binance answers again. Nothing else is paused: the
/// calls go on, so that what is open is protected and closed whenever that can be done, and so
/// that the service learns that Binance answers again. See "A Binance outage" in
/// docs/reliability.md.
/// </summary>
public class OutageWatchTests
{
    private const string Area = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceOutages";

    private const string Began =
        "Binance left {Unanswered} calls in a row without an answer on {Market} {Environment}. New entries are paused there until it answers again";

    private const string Over =
        "Binance answers again on {Market} {Environment}, after {OutageSeconds} s without an answer. New entries go on there";

    // What the file of these tests says: three calls in a row without an answer are an outage.
    private const int OutageFailures = 3;

    [Theory]
    [MemberData(nameof(BinanceCalls.MarketsAndEnvironments), MemberType = typeof(BinanceCalls))]
    public async Task Calls_in_a_row_that_binance_leaves_without_an_answer_are_an_outage_there_and_nowhere_else(Market market, TradingMode mode)
    {
        // One address of Binance cannot be reached, and the other three answer.
        using var world = new World(request => request.RequestUri!.Host == BinanceCalls.HostOf(market, mode)
            ? throw new HttpRequestException("The connection was refused.")
            : StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request)));

        // Two calls without an answer are none yet.
        await world.AskAsync(market, mode, OutageFailures - 1);

        Assert.False(world.Outages.EntriesPaused(market, mode));
        Assert.Equal(new ExchangeAnswers(market, mode, Unanswered: 2, OutageSince: null, AnsweredAt: null), world.Of(market, mode));

        // The third is one too many: Binance is out there, from this moment.
        world.Clock.Set(BinanceCalls.Now.AddSeconds(20));
        await world.AskAsync(market, mode, 1);

        Assert.True(world.Outages.EntriesPaused(market, mode));
        Assert.Equal(
            new ExchangeAnswers(market, mode, Unanswered: 3, OutageSince: BinanceCalls.Now.AddSeconds(20), AnsweredAt: null),
            world.Of(market, mode));

        // The other markets and the other environment have addresses of their own, which answer.
        foreach (var (otherMarket, otherMode) in BinanceCalls.Pairs.Where(pair => pair != (market, mode)))
        {
            await world.AskAsync(otherMarket, otherMode, 1);

            Assert.False(world.Outages.EntriesPaused(otherMarket, otherMode));
            Assert.Equal(new ExchangeAnswers(otherMarket, otherMode, 0, null, BinanceCalls.Now.AddSeconds(20)), world.Of(otherMarket, otherMode));
        }

        // It is said once, as an error, for whoever runs the service, and not with every call after it.
        await world.AskAsync(market, mode, 2);

        var said = Assert.Single(world.Said());

        Assert.Equal((Began, "Error"), (said.Template, said.Level));
        Assert.Equal(("3", market.ToString(), mode.ToString()), (said["Unanswered"].ToString(), said.Text("Market"), said.Text("Environment")));
        Assert.Equal((5, BinanceCalls.Now.AddSeconds(20)), (world.Of(market, mode).Unanswered, world.Of(market, mode).OutageSince));

        // The four are listed in a fixed order, whichever is out.
        Assert.Equal(BinanceCalls.Pairs, world.Outages.Answers().Select(answers => (answers.Market, answers.Environment)));
    }

    [Fact]
    public async Task No_call_is_paused_and_the_first_answer_ends_the_outage()
    {
        var down = true;
        using var world = new World(request => down
            ? StandInSystem.Status(HttpStatusCode.ServiceUnavailable)
            : StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request)));

        await world.AskAsync(Market.Futures, TradingMode.Live, OutageFailures);
        Assert.True(world.Outages.EntriesPaused(Market.Futures, TradingMode.Live));

        // Every call is still sent while Binance is out: the reads, and with them what protects
        // and closes a position. Nothing is held back for the outage.
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Live);
        var before = world.Binance.Calls.Count;

        var balances = await world.Exchange.GetBalancesAsync(
            BinanceCalls.User, Market.Futures, TradingMode.Live, apiKey, secretKey, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Failed, balances.Outcome);
        Assert.Equal(before + 1, world.Binance.Calls.Count);

        // A minute and a half later Binance answers a call, and that is the end of it.
        down = false;
        world.Clock.Set(BinanceCalls.Now.AddSeconds(90));
        await world.AskAsync(Market.Futures, TradingMode.Live, 1);

        Assert.False(world.Outages.EntriesPaused(Market.Futures, TradingMode.Live));
        Assert.Equal(
            new ExchangeAnswers(Market.Futures, TradingMode.Live, 0, null, BinanceCalls.Now.AddSeconds(90)),
            world.Of(Market.Futures, TradingMode.Live));

        // The end is said once as well, with how long Binance was out.
        var said = world.Said();

        Assert.Equal([(Began, "Error"), (Over, "Information")], said.Select(written => (written.Template, written.Level)));
        Assert.Equal(("Futures", "Live", "90"), (said[1].Text("Market"), said[1].Text("Environment"), said[1]["OutageSeconds"].ToString()));

        // What was counted is gone: it takes as many calls without an answer as before.
        down = true;
        await world.AskAsync(Market.Futures, TradingMode.Live, OutageFailures - 1);

        Assert.False(world.Outages.EntriesPaused(Market.Futures, TradingMode.Live));
    }

    public static TheoryData<string> WhatIsNoAnswer => ["no connection", "broken answer", "too long", "500", "502", "503", "504", "408"];

    [Theory]
    [MemberData(nameof(WhatIsNoAnswer))]
    public async Task A_call_that_reaches_nothing_or_a_fault_in_binances_place_is_no_answer(string how)
    {
        using var world = new World(_ => how switch
        {
            "no connection" => throw new HttpRequestException("The connection was refused."),
            "broken answer" => throw new IOException("The connection was reset."),
            "too long" => throw new TaskCanceledException("Binance took too long."),
            _ => StandInSystem.Json((HttpStatusCode)int.Parse(how), """{ "code": -1000, "msg": "An unknown error occurred." }"""),
        });

        await world.AskAsync(Market.Spot, TradingMode.Demo, OutageFailures);

        Assert.True(world.Outages.EntriesPaused(Market.Spot, TradingMode.Demo));
    }

    [Theory]

    // A result, a refusal of a credential, an error of Binance's own, the firewall in front of
    // it, a call that it does not have, and a clock that it does not take.
    [InlineData(HttpStatusCode.OK, BinanceCalls.Time)]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -1100, "msg": "Illegal characters found in a parameter." }""")]
    [InlineData(HttpStatusCode.Forbidden, "<html>Forbidden</html>")]
    [InlineData(HttpStatusCode.NotFound, """{ "code": -5000, "msg": "Path is invalid." }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -1021, "msg": "Timestamp for this request is outside of the recvWindow." }""")]
    public async Task Whatever_binance_itself_says_is_an_answer_and_ends_what_was_counted(HttpStatusCode status, string body)
    {
        var down = true;
        using var world = new World(_ => down ? throw new HttpRequestException("The connection was refused.") : StandInSystem.Json(status, body));

        await world.AskAsync(Market.Spot, TradingMode.Live, OutageFailures - 1);

        down = false;
        await world.AskAsync(Market.Spot, TradingMode.Live, 1);

        Assert.Equal(new ExchangeAnswers(Market.Spot, TradingMode.Live, 0, null, BinanceCalls.Now), world.Of(Market.Spot, TradingMode.Live));

        // Two without an answer, an answer, and two more: never three in a row.
        down = true;
        await world.AskAsync(Market.Spot, TradingMode.Live, OutageFailures - 1);

        Assert.False(world.Outages.EntriesPaused(Market.Spot, TradingMode.Live));
        Assert.Empty(world.Said());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)418)]
    public async Task The_signal_of_too_many_requests_is_an_answer_and_a_call_that_the_limiter_holds_back_tells_nothing(HttpStatusCode signal)
    {
        var down = true;
        using var world = new World(_ => down ? throw new HttpRequestException("The connection was refused.") : StandInSystem.Status(signal));

        await world.AskAsync(Market.Futures, TradingMode.Demo, OutageFailures - 1);

        // Binance says that it has had too many requests: it is there, and the limiter sees to it.
        down = false;
        Assert.Equal(
            ExchangeCallOutcome.RateLimited,
            (await world.Exchange.GetServerTimeAsync(Market.Futures, TradingMode.Demo, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(0, world.Of(Market.Futures, TradingMode.Demo).Unanswered);

        // From then on the limiter sends nothing there for a while. A call that was not sent was
        // left without no answer: however many there are, they are no outage.
        var sent = world.Binance.Calls.Count;

        for (var call = 0; call < 2 * OutageFailures; call++)
        {
            Assert.Equal(
                ExchangeCallOutcome.RateLimited,
                (await world.Exchange.GetServerTimeAsync(Market.Futures, TradingMode.Demo, TestContext.Current.CancellationToken)).Outcome);
        }

        Assert.Equal(sent, world.Binance.Calls.Count);
        Assert.Equal(new ExchangeAnswers(Market.Futures, TradingMode.Demo, 0, null, BinanceCalls.Now), world.Of(Market.Futures, TradingMode.Demo));
        Assert.Empty(world.Said());
    }

    [Fact]
    public async Task A_call_that_its_caller_cancels_tells_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        using var world = new World(_ => throw new OperationCanceledException(cancelled.Token));

        await cancelled.CancelAsync();

        for (var call = 0; call < 2 * OutageFailures; call++)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => world.Exchange.GetServerTimeAsync(Market.Spot, TradingMode.Live, cancelled.Token));
        }

        Assert.Equal(new ExchangeAnswers(Market.Spot, TradingMode.Live, 0, null, null), world.Of(Market.Spot, TradingMode.Live));
    }

    [Fact]
    public async Task The_call_that_asks_what_a_key_may_do_is_not_counted()
    {
        // Binance's wallet API is down, at the address of Spot on the real exchange, and the
        // market itself answers.
        using var world = new World(request => request.RequestUri!.AbsolutePath.StartsWith("/sapi/", StringComparison.Ordinal)
            ? StandInSystem.Status(HttpStatusCode.ServiceUnavailable)
            : StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request)));
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Live);

        for (var check = 0; check < 2 * OutageFailures; check++)
        {
            await world.Exchange.CheckKeyAsync(BinanceCalls.User, TradingMode.Live, apiKey, secretKey, TestContext.Current.CancellationToken);
        }

        // The wallet API was called, and says nothing of whether the market takes orders.
        Assert.Contains(world.Binance.Calls, call => call.Address.AbsolutePath.StartsWith("/sapi/", StringComparison.Ordinal));
        Assert.Equal((0, null), (world.Of(Market.Spot, TradingMode.Live).Unanswered, world.Of(Market.Spot, TradingMode.Live).OutageSince));
        Assert.Empty(world.Said());
    }

    [Fact]
    public async Task The_reading_of_the_clocks_finds_that_binance_answers_again()
    {
        // An application that reads Binance's clocks on its schedule, on a database of its own.
        var down = false;
        using var world = new World(
            request => down
                ? throw new HttpRequestException("The connection was refused.")
                : StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request)),
            watchesClock: true);

        await world.ClocksReadAsync();

        // Binance goes out everywhere: the readings of its four clocks are what finds it, with
        // nothing else calling.
        down = true;

        for (var reading = 0; reading < OutageFailures; reading++)
        {
            await world.ReadClocksAsync();
        }

        Assert.All(BinanceCalls.Pairs, pair => Assert.True(world.Outages.EntriesPaused(pair.Market, pair.Mode)));

        // And the next reading after it is back is what finds that: the calls are made again
        // later, with nobody asking for them (NFR-REL-4).
        down = false;
        await world.ReadClocksAsync();

        Assert.All(BinanceCalls.Pairs, pair => Assert.False(world.Outages.EntriesPaused(pair.Market, pair.Mode)));
        Assert.Equal(4, world.Said().Count(said => said.Template == Over));
    }

    [Fact]
    public void What_places_an_entry_asks_whether_binance_is_out()
    {
        // The watch pauses nothing by itself: what places an entry asks, as it asks the watch on
        // the clock. That no entry is placed during an outage is in PlanOrderTests.
        var asks = typeof(IExchangeOutages).Assembly.GetTypes()
            .Where(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IExchangeOutages)))
            .Select(type => type.FullName);

        Assert.Contains("Ecnanib.DecisionEngine.AgentOrders", asks);
    }

    /// <summary>
    /// The gateway of an application, with a Binance of the test's behind it, a clock that the
    /// test moves, and a file in which three calls in a row without an answer are an outage.
    /// </summary>
    private sealed class World : IDisposable
    {
        private const string ClockJob = "clock-watch";

        private readonly TestDatabase? _database;

        public World(Func<HttpRequestMessage, HttpResponseMessage> answer, bool watchesClock = false)
        {
            _database = watchesClock ? PostgresServer.CreateDatabase() : null;
            Binance = new StandInSystem(answer);
            Application = BinanceCalls.Behind(
                Binance,
                clock: Clock,
                database: _database,
                watchesClock: watchesClock,
                file: file => file.With("Binance:OutageFailures", OutageFailures));
            Exchange = Application.Services.GetRequiredService<IExchangeAdapter>();
            Outages = Application.Services.GetRequiredService<IExchangeOutages>();
        }

        public TestClock Clock { get; } = new(BinanceCalls.Now);

        public StandInSystem Binance { get; }

        public LoggedApplication Application { get; }

        public IExchangeAdapter Exchange { get; }

        public IExchangeOutages Outages { get; }

        public ExchangeAnswers Of(Market market, TradingMode mode) =>
            Outages.Answers().Single(answers => answers.Market == market && answers.Environment == mode);

        /// <summary>What the watch on outages wrote to the log.</summary>
        public IReadOnlyList<LoggedEvent> Said() => [.. Application.Events().Where(written => written.Area == Area)];

        /// <summary>Makes as many calls as it says on a market in an environment, one after the other: each asks for Binance's time there.</summary>
        public async Task AskAsync(Market market, TradingMode mode, int calls)
        {
            for (var call = 0; call < calls; call++)
            {
                await Exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken);
            }
        }

        /// <summary>Waits for the first reading of the clocks, which is due as the background work starts.</summary>
        public Task ClocksReadAsync() =>
            Wait.UntilAsync(
                () => StoredJob.Of(_database!, ClockJob) is { Outcome: not null }, "the first reading of Binance's clocks");

        /// <summary>Reads the clocks now, as the scheduler does when the reading is due.</summary>
        public Task ReadClocksAsync() =>
            Application.Services.GetServices<IBackgroundJob>()
                .Single(job => job.Name == ClockJob)
                .RunAsync(new BackgroundJobRun(Clock.GetUtcNow()), TestContext.Current.CancellationToken);

        public void Dispose() => Application.Dispose();
    }
}
