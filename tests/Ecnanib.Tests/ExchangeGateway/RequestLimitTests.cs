using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Binance counts its limits on requests against the address that the calls come from, so all
/// users of the service spend one budget. One limiter governs every call to Binance: it keeps
/// the service under the part of each limit that the configuration file allows it, sends nothing
/// while Binance signals that it has had too many, gives the users whose calls wait their turns,
/// and says what is used (FR-BIN-4).
/// </summary>
/// <remarks>
/// The clock is the test's, with the timers that run on it: a minute passes when a test says so.
/// With <c>Binance:LimitSharePercent</c> at 10, the calls of the Futures market may weigh 240 in
/// a minute: 48 at once, and 3.2 more in each second. A read of its balances weighs 5.
/// </remarks>
public class RequestLimitTests
{
    private const string LimiterArea = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceLimiter";

    private const string SecretKey = "binance-sealed-0123456789-abcdefghij-Zz9Y";

    private const string TooMany = """{ "code": -1003, "msg": "Too much request weight used." }""";

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    // How many reads of the Futures balances pass at once with a part of 10 percent: 45 of the
    // 48 that may. What is left is 3.
    private const int ReadsAtOnce = 9;

    [Fact]
    public async Task Every_call_to_binance_for_every_user_passes_the_one_limiter_which_counts_its_weight()
    {
        using var service = new Service();

        foreach (var user in new[] { 1, 2 })
        {
            foreach (var mode in new[] { TradingMode.Live, TradingMode.Demo })
            {
                await service.CheckKeyAsync(user, mode);

                foreach (var market in new[] { Market.Spot, Market.Futures })
                {
                    await service.TimeAsync(market, mode);
                    await service.BalancesAsync(user, market, mode);
                }
            }
        }

        // One limiter, for everything.
        Assert.Single(service.Application.Services.GetServices<IExchangeLimits>());

        // What reached Binance, by the limit that Binance counts it against, with the weight that
        // its documentation gives each call. The limiter has counted every call of it.
        var reached = service.Arrivals
            .GroupBy(call => (Limit: LimitOf(call.Path), Environment: EnvironmentOf(call.Host)))
            .ToDictionary(calls => calls.Key, calls => calls.Sum(call => WeightOf(call.Path)));
        var usage = service.Limits.Usage();

        Assert.Equal(5, reached.Count);
        Assert.All(usage, limit => Assert.Equal(reached[(limit.Name, limit.Environment)], limit.Used));

        // For two users: the clock and the account of Spot, the same and the check of a key in
        // the test environment, the same of Futures, and the permissions of a key.
        Assert.Equal([42, 82, 12, 22, 2], usage.Select(limit => limit.Used));
    }

    [Fact]
    public async Task The_limiter_says_what_each_limit_allows_and_what_is_used_of_it()
    {
        // Binance says what it has counted with each answer: its wallet API under a name of its own.
        using var service = new Service(share: 70, answer: request =>
        {
            var answer = Service.Documented(request);
            var wallet = request.RequestUri!.AbsolutePath.StartsWith("/sapi/", StringComparison.Ordinal);
            answer.Headers.Add(wallet ? "X-SAPI-USED-IP-WEIGHT-1M" : "X-MBX-USED-WEIGHT-1M", wallet ? "3" : "37");

            return answer;
        });

        // Each limit of Binance in each environment that has it, with the part of it that the
        // configuration file allows the service.
        Assert.Equal(
            [
                new ExchangeLimitUsage("Spot", TradingMode.Live, 6000, 4200, 0, null, null, 0, null),
                new ExchangeLimitUsage("Spot", TradingMode.Demo, 6000, 4200, 0, null, null, 0, null),
                new ExchangeLimitUsage("Futures", TradingMode.Live, 2400, 1680, 0, null, null, 0, null),
                new ExchangeLimitUsage("Futures", TradingMode.Demo, 2400, 1680, 0, null, null, 0, null),
                new ExchangeLimitUsage("Key permissions", TradingMode.Live, 12000, 8400, 0, null, null, 0, null),
            ],
            service.Limits.Usage());

        await service.BalancesAsync(1, Market.Spot, TradingMode.Live);
        await service.CheckKeyAsync(1, TradingMode.Live);

        // What the limiter let through, and what Binance said it has counted, and when.
        var usage = service.Limits.Usage();
        Assert.Equal(new ExchangeLimitUsage("Spot", TradingMode.Live, 6000, 4200, 20, 37, BinanceCalls.Now, 0, null), usage[0]);
        Assert.Equal(new ExchangeLimitUsage("Key permissions", TradingMode.Live, 12000, 8400, 1, 3, BinanceCalls.Now, 0, null), usage[4]);
        Assert.All(usage.Skip(1).Take(3), limit => Assert.Equal((0, null), (limit.Used, limit.Reported)));

        // What is used is that of the last minute.
        await service.AdvanceAsync(61);

        Assert.Equal(
            new ExchangeLimitUsage("Spot", TradingMode.Live, 6000, 4200, 0, 37, BinanceCalls.Now, 0, null), service.Limits.Usage()[0]);
    }

    [Fact]
    public async Task The_weight_of_any_minute_stays_under_the_part_that_the_service_allows_itself_and_users_get_equal_parts()
    {
        using var service = new Service(share: 10);
        var calls = new List<Task<ExchangeBalances>>();

        // Three users ask for far more than there is, for three minutes: eight reads in each
        // second, where the limit has room for two in three seconds.
        for (var step = 0; step < 360; step++)
        {
            for (var call = 0; call < 4; call++)
            {
                calls.Add(service.BalancesAsync(1 + (((step * 4) + call) % 3)));
            }

            await service.AdvanceAsync(0.5);
        }

        // The calls that still wait have their turn, or give up.
        await service.AdvanceAsync(10);
        var outcomes = (await Task.WhenAll(calls)).Select(read => read.Outcome).ToList();

        // A call was sent and answered, or it was not sent.
        var sent = service.Arrivals;
        Assert.Equal(sent.Count, outcomes.Count(outcome => outcome == ExchangeCallOutcome.Succeeded));
        Assert.Equal(calls.Count - sent.Count, outcomes.Count(outcome => outcome == ExchangeCallOutcome.RateLimited));

        // Whichever sixty seconds are taken, what reached Binance in them is under the part of
        // its limit that the service allows itself: a tenth of 2400.
        var allowed = service.UsageOf("Futures", TradingMode.Live).Allowed;
        Assert.Equal(240, allowed);
        Assert.All(sent, last => Assert.InRange(
            sent.Where(call => call.At > last.At - Minute && call.At <= last.At).Sum(call => WeightOf(call.Path)), 1, allowed));

        // And it is used, not kept: what may go at once, and what flowed in three minutes.
        Assert.InRange(sent.Sum(call => WeightOf(call.Path)), 600, 660);

        // Each of the three users got a third of it, to the call.
        var parts = sent.GroupBy(call => call.ApiKey).Select(user => user.Count()).ToList();
        Assert.Equal(3, parts.Count);
        Assert.InRange(parts.Max() - parts.Min(), 0, 2);
    }

    [Fact]
    public async Task Each_limit_is_counted_on_its_own_so_one_that_is_used_up_leaves_the_others()
    {
        using var service = new Service(share: 10);

        // The Futures market of the test environment has room for nine reads at once.
        var reads = Enumerable.Range(0, ReadsAtOnce + 1).Select(_ => service.BalancesAsync(1, Market.Futures, TradingMode.Demo)).ToList();
        await service.SettleAsync();

        Assert.Equal(ReadsAtOnce, reads.Count(read => read.IsCompleted));
        Assert.Equal((45, 1), (service.UsageOf("Futures", TradingMode.Demo).Used, service.UsageOf("Futures", TradingMode.Demo).Waiting));

        // The real exchange, the Spot market, and the permissions of a key are other limits.
        var elsewhere = new[]
        {
            (await service.BalancesAsync(1, Market.Futures, TradingMode.Live)).Outcome,
            (await service.BalancesAsync(1, Market.Spot, TradingMode.Demo)).Outcome,
            (await service.TimeAsync(Market.Spot, TradingMode.Live)).Outcome,
        };
        Assert.All(elsewhere, outcome => Assert.Equal(ExchangeCallOutcome.Succeeded, outcome));
        Assert.Equal(KeyVerdict.Accepted, (await service.CheckKeyAsync(1, TradingMode.Live)).Verdict);

        // The tenth read still waits, and is made when there is room for it.
        Assert.False(reads[^1].IsCompleted);

        await service.AdvanceAsync(1);

        Assert.Equal(ExchangeCallOutcome.Succeeded, (await reads[^1]).Outcome);
    }

    [Fact]
    public async Task Calls_that_wait_take_turns_user_by_user_so_the_burst_of_one_user_does_not_hold_up_another()
    {
        using var service = new Service(share: 10);

        // One user asks for twelve reads at once. Nine pass, and three wait.
        var burst = Enumerable.Range(0, ReadsAtOnce + 3).Select(_ => service.BalancesAsync(1)).ToList();
        await service.SettleAsync();
        Assert.Equal(ReadsAtOnce, service.Arrivals.Count);

        // Then another user asks for one.
        var other = service.BalancesAsync(2);
        await service.SettleAsync();
        Assert.Equal(4, service.UsageOf("Futures", TradingMode.Live).Waiting);

        // There is room for one call about every second and a half. The first is the next of the
        // burst, which waited first, and the second is the other user's: not the rest of the burst.
        await service.AdvanceAsync(0.7);
        Assert.Equal(ReadsAtOnce + 1, service.Arrivals.Count);

        await service.AdvanceAsync(1.6);
        Assert.Equal(ReadsAtOnce + 2, service.Arrivals.Count);
        Assert.Equal(ExchangeCallOutcome.Succeeded, (await other).Outcome);
        Assert.Equal(2, burst.Count(read => !read.IsCompleted));

        await service.AdvanceAsync(1.6);
        await service.AdvanceAsync(1.6);

        Assert.All(await Task.WhenAll(burst), read => Assert.Equal(ExchangeCallOutcome.Succeeded, read.Outcome));
        Assert.Equal(
            [Service.ApiKeyOf(1), Service.ApiKeyOf(2), Service.ApiKeyOf(1), Service.ApiKeyOf(1)],
            service.Arrivals.Skip(ReadsAtOnce).Select(call => call.ApiKey));
    }

    [Fact]
    public async Task A_call_whose_turn_cannot_come_within_ten_seconds_is_not_sent_and_ends_at_once()
    {
        // Two hundredths of Binance's limit, the least that the configuration file takes: 48
        // in a minute. What may go at once is the heaviest call of the market, the read of
        // funding at 30, which is six reads of the balances, and then one in seventeen seconds.
        using var service = new Service(share: 2);

        for (var read = 0; read < 6; read++)
        {
            Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1)).Outcome);
        }

        // No time passes: the limiter knows already.
        var held = await service.BalancesAsync(1);

        Assert.Equal(ExchangeCallOutcome.RateLimited, held.Outcome);
        Assert.Empty(held.Balances);
        Assert.Equal(6, service.Arrivals.Count);
        Assert.Equal(0, service.UsageOf("Futures", TradingMode.Live).Waiting);

        // It is in no log of outbound calls, which has what was sent. The gateway says it.
        var said = Assert.Single(service.Application.Events(), written => written.Area == BinanceCalls.GatewayArea);
        Assert.Equal("Warning", said.Level);
        Assert.Equal("fapi/v3/balance", said.Text("Path"));
        Assert.Equal("Futures", said.Text("Market"));
        Assert.Equal("Live", said.Text("Environment"));

        // The same call passes when there is room for it again.
        await service.AdvanceAsync(17);

        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1)).Outcome);
    }

    [Fact]
    public async Task A_call_that_waits_gives_up_after_ten_seconds_and_is_not_sent()
    {
        using var service = new Service(share: 10);

        // The room that there is at once is used, to the last two of request weight.
        await Task.WhenAll(Enumerable.Range(0, ReadsAtOnce).Select(_ => service.BalancesAsync(1)));
        await service.TimeAsync(Market.Futures, TradingMode.Live);

        // Seven users ask for one read each. For each alone, room would come in a second. With
        // turns, the seventh would have its turn after ten seconds and a third.
        var reads = Enumerable.Range(2, 7).Select(user => service.BalancesAsync(user)).ToList();
        await service.SettleAsync();
        Assert.Equal(7, service.UsageOf("Futures", TradingMode.Live).Waiting);

        await service.AdvanceAsync(9);

        Assert.All(await Task.WhenAll(reads.Take(6)), read => Assert.Equal(ExchangeCallOutcome.Succeeded, read.Outcome));
        Assert.False(reads[^1].IsCompleted);

        // Ten seconds after it began to wait, it ends, and Binance has not seen it.
        await service.AdvanceAsync(1);

        Assert.Equal(ExchangeCallOutcome.RateLimited, (await reads[^1]).Outcome);
        Assert.Equal(ReadsAtOnce + 1 + 6, service.Arrivals.Count);
        Assert.Equal(0, service.UsageOf("Futures", TradingMode.Live).Waiting);
    }

    [Fact]
    public async Task A_call_whose_caller_cancels_while_it_waits_ends_as_cancelled_and_is_not_sent()
    {
        using var service = new Service(share: 10);
        await Task.WhenAll(Enumerable.Range(0, ReadsAtOnce).Select(_ => service.BalancesAsync(1)));

        using var cancelled = new CancellationTokenSource();
        var read = service.BalancesAsync(2, cancellationToken: cancelled.Token);
        await service.SettleAsync();
        Assert.Equal(1, service.UsageOf("Futures", TradingMode.Live).Waiting);

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(ReadsAtOnce, service.Arrivals.Count);
        Assert.Equal(0, service.UsageOf("Futures", TradingMode.Live).Waiting);
    }

    [Fact]
    public async Task A_call_that_waited_says_that_it_was_made_when_it_was_sent()
    {
        using var service = new Service(share: 10);
        await Task.WhenAll(Enumerable.Range(0, ReadsAtOnce).Select(_ => service.BalancesAsync(1)));

        var read = service.BalancesAsync(1);
        await service.AdvanceAsync(2);

        Assert.Equal(ExchangeCallOutcome.Succeeded, (await read).Outcome);

        // Binance takes a signed call for five seconds after the time that it carries. The time
        // is that of the sending, and the signature is over it.
        var call = service.Binance.Calls[^1];
        Assert.Equal((BinanceCalls.Now + TimeSpan.FromSeconds(2)).ToUnixTimeMilliseconds().ToString(), call.Query[^2].Value);
        Assert.Equal("timestamp", call.Query[^2].Name);
        Assert.Equal(BinanceCalls.SignatureOf(call, SecretKey), call.Query[^1].Value);
    }

    [Fact]
    public async Task When_binance_says_that_it_has_had_too_many_requests_nothing_is_sent_until_the_time_it_names_has_passed()
    {
        var throttling = true;
        using var service = new Service(answer: request =>
        {
            if (!throttling || !request.RequestUri!.Host.StartsWith("futures-live.", StringComparison.Ordinal))
            {
                return null;
            }

            var answer = StandInSystem.Json(HttpStatusCode.TooManyRequests, TooMany);
            answer.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

            return answer;
        });

        // The call that Binance answers so is made once, and ends as what it is.
        var first = await service.BalancesAsync(1);

        Assert.Equal(ExchangeCallOutcome.RateLimited, first.Outcome);
        Assert.Empty(first.Balances);
        Assert.Single(service.Arrivals);

        // The limit of that market in that environment is held, for as long as Binance said.
        Assert.Equal(BinanceCalls.Now + TimeSpan.FromSeconds(30), service.UsageOf("Futures", TradingMode.Live).HeldUntil);
        Assert.All(
            service.Limits.Usage().Where(limit => limit != service.UsageOf("Futures", TradingMode.Live)),
            limit => Assert.Null(limit.HeldUntil));

        var said = Assert.Single(service.Application.Events(), written => written.Area == LimiterArea);
        Assert.Equal("Warning", said.Level);
        Assert.Equal("Futures", said.Text("Limit"));
        Assert.Equal("Live", said.Text("Environment"));
        Assert.Equal("Throttled", said.Text("Signal"));
        Assert.Equal(30, said["HoldSeconds"].GetDouble());

        // However often a caller asks again, and whoever asks, Binance sees nothing of it: no
        // call is made again in a loop. Binance would take a call by now, and gets none.
        throttling = false;
        for (var again = 0; again < 60; again++)
        {
            Assert.Equal(ExchangeCallOutcome.RateLimited, (await service.BalancesAsync(1 + (again % 3))).Outcome);
            Assert.Equal(ExchangeCallOutcome.RateLimited, (await service.TimeAsync(Market.Futures, TradingMode.Live)).Outcome);
        }

        Assert.Single(service.Arrivals);

        // The other limits are not held.
        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1, Market.Futures, TradingMode.Demo)).Outcome);
        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.TimeAsync(Market.Spot, TradingMode.Live)).Outcome);
        Assert.Equal(3, service.Arrivals.Count);

        // Five seconds before the end, a call waits for it, and is not sent yet.
        await service.AdvanceAsync(25);
        var waiting = service.BalancesAsync(2);
        await service.SettleAsync();

        Assert.False(waiting.IsCompleted);
        Assert.Equal(3, service.Arrivals.Count);

        // When the time has passed, calls are sent again.
        await service.AdvanceAsync(6);

        Assert.Equal(ExchangeCallOutcome.Succeeded, (await waiting).Outcome);
        Assert.Equal(4, service.Arrivals.Count);
        Assert.Null(service.UsageOf("Futures", TradingMode.Live).HeldUntil);
    }

    [Theory]

    // Too many requests, and the block of the address that follows when that is ignored.
    [InlineData(HttpStatusCode.TooManyRequests, 60, "Throttled")]
    [InlineData((HttpStatusCode)418, 120, "Banned")]
    public async Task Where_binance_names_no_time_the_limiter_holds_for_longer_each_time_the_signal_comes_again(
        HttpStatusCode status, int seconds, string signal)
    {
        var signalling = true;
        using var service = new Service(answer: _ => signalling ? StandInSystem.Json(status, TooMany) : null);

        // A minute, or two after a block, and twice as long each time that the first call after
        // it is answered the same.
        var sent = 0;
        foreach (var hold in new[] { seconds, 2 * seconds, 4 * seconds })
        {
            Assert.Equal(ExchangeCallOutcome.RateLimited, (await service.BalancesAsync(1)).Outcome);
            Assert.Equal(++sent, service.Arrivals.Count);
            Assert.Equal(service.Clock.GetUtcNow() + TimeSpan.FromSeconds(hold), service.UsageOf("Futures", TradingMode.Live).HeldUntil);

            // Nothing is sent meanwhile, to the last seconds of it.
            await service.AdvanceAsync(hold - 15);
            for (var again = 0; again < 20; again++)
            {
                Assert.Equal(ExchangeCallOutcome.RateLimited, (await service.BalancesAsync(1 + (again % 3))).Outcome);
            }

            Assert.Equal(sent, service.Arrivals.Count);

            await service.AdvanceAsync(16);
        }

        Assert.Equal(
            [signal, signal, signal],
            service.Application.Events().Where(written => written.Area == LimiterArea).Select(written => written.Text("Signal")));

        // An answer that is no signal ends the row: the next signal holds as the first did.
        signalling = false;
        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1)).Outcome);
        Assert.Null(service.UsageOf("Futures", TradingMode.Live).HeldUntil);

        signalling = true;
        Assert.Equal(ExchangeCallOutcome.RateLimited, (await service.BalancesAsync(1)).Outcome);
        Assert.Equal(service.Clock.GetUtcNow() + TimeSpan.FromSeconds(seconds), service.UsageOf("Futures", TradingMode.Live).HeldUntil);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, TooMany)]
    [InlineData((HttpStatusCode)418, """{ "code": -1003, "msg": "Way too much request weight used; IP banned." }""")]
    public async Task The_signal_of_binance_is_an_outcome_of_its_own_which_says_nothing_about_a_credential(HttpStatusCode status, string body)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(status, body));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

            // Not a failure like any other, and not the user's key being wrong.
            var balances = await exchange.GetBalancesAsync(
                BinanceCalls.User, market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);
            Assert.Equal(ExchangeCallOutcome.RateLimited, balances.Outcome);
            Assert.Empty(balances.Balances);

            // The clock of the market is counted against the same limit, which is held now.
            Assert.Equal(
                new ExchangeTime(ExchangeCallOutcome.RateLimited, null),
                await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken));
        }

        // Each market in each environment got the one call that was answered with the signal.
        Assert.Equal(4, binance.Calls.Count);

        // The check of a key says nothing about the key: no stored key turns invalid for it. On
        // the real exchange it asks the wallet API, which is a limit of its own. In the test
        // environment it asks the two markets, which are held, so nothing is sent.
        foreach (var mode in new[] { TradingMode.Live, TradingMode.Demo })
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

            Assert.Equal(
                ExchangeKeyCheck.Unreachable,
                await exchange.CheckKeyAsync(BinanceCalls.User, mode, apiKey, secretKey, TestContext.Current.CancellationToken));
        }

        Assert.Equal(5, binance.Calls.Count);

        // The gateway says which error it was, as of any other answer that is no success.
        var errors = application.Events().Where(written => written.Area == BinanceCalls.GatewayArea && written.Has("BinanceCode")).ToList();
        Assert.Equal(5, errors.Count);
        Assert.All(errors, said => Assert.Equal(-1003, said["BinanceCode"].GetInt32()));
    }

    [Fact]
    public async Task A_read_that_nothing_depends_on_has_room_while_less_than_half_of_the_part_is_used_and_no_call_waits()
    {
        using var service = new Service(share: 10);

        // Nothing is used. Then nine reads at once: 45 of the 240 that a minute allows the service.
        Assert.True(service.Limits.HasRoom(Market.Futures, TradingMode.Live));

        var reads = Enumerable.Range(0, ReadsAtOnce).Select(_ => service.BalancesAsync(1)).ToList();
        await service.SettleAsync();

        Assert.True(service.Limits.HasRoom(Market.Futures, TradingMode.Live));

        // A tenth read waits for its turn. What waits is what an agent asked for, and goes first.
        var waits = service.BalancesAsync(1);
        await service.SettleAsync();

        Assert.Equal(1, service.UsageOf("Futures", TradingMode.Live).Waiting);
        Assert.False(service.Limits.HasRoom(Market.Futures, TradingMode.Live));

        // Each limit is asked about on its own.
        Assert.True(service.Limits.HasRoom(Market.Futures, TradingMode.Demo));
        Assert.True(service.Limits.HasRoom(Market.Spot, TradingMode.Live));

        // More is asked for than flows in, for half a minute, and then the calls that wait have
        // their turn or give up: nothing waits, and half of the part is used.
        for (var step = 0; step < 60; step++)
        {
            reads.Add(service.BalancesAsync(1));
            await service.AdvanceAsync(0.5);
        }

        await service.AdvanceAsync(10);
        await waits;
        await Task.WhenAll(reads);

        var used = service.UsageOf("Futures", TradingMode.Live);
        Assert.Equal(0, used.Waiting);
        Assert.InRange(used.Used, used.Allowed / 2, used.Allowed);
        Assert.False(service.Limits.HasRoom(Market.Futures, TradingMode.Live));

        // A minute later that is no longer of the minute.
        await service.AdvanceAsync(60);

        Assert.True(service.Limits.HasRoom(Market.Futures, TradingMode.Live));
    }

    [Fact]
    public async Task A_read_that_nothing_depends_on_has_no_room_while_binance_has_had_too_many_or_has_counted_half_of_the_part()
    {
        var throttling = true;
        var counted = 0;
        using var service = new Service(share: 10, answer: request =>
        {
            if (request.RequestUri!.Host.StartsWith("futures-live.", StringComparison.Ordinal))
            {
                if (!throttling)
                {
                    return null;
                }

                var answer = StandInSystem.Json(HttpStatusCode.TooManyRequests, TooMany);
                answer.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

                return answer;
            }

            // The test environment says what it has counted, which may be more than the limiter did.
            var documented = Service.Documented(request);
            documented.Headers.Add("X-MBX-USED-WEIGHT-1M", counted.ToString());

            return documented;
        });

        // Binance signalled that it has had too many: the limit is held for as long as it said.
        Assert.Equal(ExchangeCallOutcome.RateLimited, (await service.BalancesAsync(1)).Outcome);
        Assert.False(service.Limits.HasRoom(Market.Futures, TradingMode.Live));

        throttling = false;
        await service.AdvanceAsync(30);

        Assert.True(service.Limits.HasRoom(Market.Futures, TradingMode.Live));

        // Binance has counted 119 of the 240, and then 120: another process at the same address.
        counted = 119;
        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1, Market.Futures, TradingMode.Demo)).Outcome);
        Assert.True(service.Limits.HasRoom(Market.Futures, TradingMode.Demo));

        counted = 120;
        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1, Market.Futures, TradingMode.Demo)).Outcome);
        Assert.False(service.Limits.HasRoom(Market.Futures, TradingMode.Demo));

        // What it counted is of its minute, and says nothing of the next.
        await service.AdvanceAsync(60);

        Assert.True(service.Limits.HasRoom(Market.Futures, TradingMode.Demo));
    }

    [Fact]
    public async Task When_binance_has_counted_more_than_the_limiter_the_limiter_goes_by_what_binance_counts()
    {
        // More than the 240 that the service allows itself, of which the limiter has let through
        // five: another process at the same address, or a call that weighs more than documented.
        var counted = 400;
        using var service = new Service(share: 10, answer: request =>
        {
            var answer = Service.Documented(request);
            answer.Headers.Add("X-MBX-USED-WEIGHT-1M", counted.ToString());

            return answer;
        });

        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1)).Outcome);

        var usage = service.UsageOf("Futures", TradingMode.Live);
        Assert.Equal((5, 400, BinanceCalls.Now), (usage.Used, usage.Reported, usage.ReportedAt));

        // There is no room until what Binance has counted too much has flowed back in, which is
        // longer than a call waits.
        for (var again = 0; again < 5; again++)
        {
            Assert.Equal(ExchangeCallOutcome.RateLimited, (await service.BalancesAsync(1)).Outcome);
        }

        Assert.Single(service.Arrivals);

        // It is said once, and not with every answer.
        var said = Assert.Single(service.Application.Events(), written => written.Area == LimiterArea);
        Assert.Equal("Warning", said.Level);
        Assert.Equal(400, said["Reported"].GetInt32());
        Assert.Equal("Futures", said.Text("Limit"));
        Assert.Equal("Live", said.Text("Environment"));

        // A minute later Binance counts anew, and calls pass again.
        counted = 12;
        await service.AdvanceAsync(60);

        Assert.Equal(ExchangeCallOutcome.Succeeded, (await service.BalancesAsync(1)).Outcome);
        Assert.Equal(12, service.UsageOf("Futures", TradingMode.Live).Reported);
        Assert.Single(service.Application.Events(), written => written.Area == LimiterArea);
    }

    /// <summary>The limit that Binance counts a call against, by its path, under the name that the limiter reports it by.</summary>
    private static string LimitOf(string path) => path switch
    {
        _ when path.StartsWith("/sapi/", StringComparison.Ordinal) => "Key permissions",
        _ when path.StartsWith("/api/", StringComparison.Ordinal) => "Spot",
        _ when path.StartsWith("/fapi/", StringComparison.Ordinal) => "Futures",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
    };

    private static TradingMode EnvironmentOf(string host) =>
        host.Contains("-live.", StringComparison.Ordinal) ? TradingMode.Live : TradingMode.Demo;

    /// <summary>The request weight that Binance's documentation gives a call.</summary>
    private static int WeightOf(string path) => path switch
    {
        "/api/v3/time" or "/fapi/v1/time" or "/sapi/v1/account/apiRestrictions" => 1,
        "/fapi/v1/accountConfig" or "/fapi/v3/balance" => 5,
        "/api/v3/account" => 20,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
    };

    /// <summary>
    /// The application behind a Binance of the test's, on a clock that only the test moves. It
    /// keeps every call that a test makes, so that a test can wait until each has ended or
    /// waits for its turn, and move the clock only then.
    /// </summary>
    private sealed class Service : IDisposable
    {
        // The users, by number from one. A user without a number is none.
        private static readonly Guid[] Users =
            [.. Enumerable.Range(0, 10).Select(number => new Guid($"00000000-0000-4000-8000-00000000000{number}"))];

        private readonly List<Arrival> _arrivals = [];
        private readonly List<Task> _calls = [];

        /// <param name="share">The part of Binance's limits that the service allows itself, in percent, when it is not that of the usual file.</param>
        /// <param name="answer">What Binance answers a call with, when it is not what its documentation gives: null is that.</param>
        public Service(int? share = null, Func<HttpRequestMessage, HttpResponseMessage?>? answer = null)
        {
            Clock = new FakeTimeProvider(BinanceCalls.Now);
            Binance = new StandInSystem(request =>
            {
                lock (_arrivals)
                {
                    _arrivals.Add(new Arrival(
                        Clock.GetUtcNow(),
                        request.RequestUri!.Host,
                        request.RequestUri.AbsolutePath,
                        request.Headers.TryGetValues("X-MBX-APIKEY", out var keys) ? keys.Single() : null));
                }

                return answer?.Invoke(request) ?? Documented(request);
            });
            Application = BinanceCalls.Behind(Binance, clock: Clock, share: share);
            Exchange = Application.Services.GetRequiredService<IExchangeAdapter>();
            Limits = Application.Services.GetRequiredService<IExchangeLimits>();
        }

        public FakeTimeProvider Clock { get; }

        public StandInSystem Binance { get; }

        public LoggedApplication Application { get; }

        public IExchangeAdapter Exchange { get; }

        public IExchangeLimits Limits { get; }

        /// <summary>The calls that reached Binance, in the order they came, each with the time that the clock showed.</summary>
        public IReadOnlyList<Arrival> Arrivals
        {
            get
            {
                lock (_arrivals)
                {
                    return [.. _arrivals];
                }
            }
        }

        /// <summary>What Binance's documentation gives as the answer to a call.</summary>
        public static HttpResponseMessage Documented(HttpRequestMessage request) =>
            StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request.RequestUri!.AbsolutePath));

        /// <summary>The API key of a user. Each has one of its own, by which Binance's side can tell whose call it got.</summary>
        public static string ApiKeyOf(int user) => $"binance-api-of-user-{user}-0123456789-abcd-Lm3N";

        public Task<ExchangeBalances> BalancesAsync(
            int user, Market market = Market.Futures, TradingMode mode = TradingMode.Live, CancellationToken? cancellationToken = null) =>
            Made(Exchange.GetBalancesAsync(
                Users[user], market, mode, ApiKeyOf(user), SecretKey, cancellationToken ?? TestContext.Current.CancellationToken));

        public Task<ExchangeTime> TimeAsync(Market market, TradingMode mode) =>
            Made(Exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken));

        public Task<ExchangeKeyCheck> CheckKeyAsync(int user, TradingMode mode) =>
            Made(Exchange.CheckKeyAsync(Users[user], mode, ApiKeyOf(user), SecretKey, TestContext.Current.CancellationToken));

        public ExchangeLimitUsage UsageOf(string name, TradingMode mode) =>
            Limits.Usage().Single(limit => limit.Name == name && limit.Environment == mode);

        /// <summary>Waits until every call that was made has ended, or waits for its turn at the limiter.</summary>
        public async Task SettleAsync()
        {
            var waited = Stopwatch.StartNew();

            while (true)
            {
                int made, ended;
                lock (_calls)
                {
                    made = _calls.Count;
                    ended = _calls.Count(call => call.IsCompleted);
                }

                if (ended + Limits.Usage().Sum(limit => limit.Waiting) == made)
                {
                    return;
                }

                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "The calls neither ended nor waited for their turn.");

                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        }

        /// <summary>Moves the clock, when the calls have settled, and lets what follows from it settle.</summary>
        public async Task AdvanceAsync(double seconds)
        {
            await SettleAsync();
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            await SettleAsync();
        }

        public void Dispose() => Application.Dispose();

        // On a clock that stands still, a call that waits for a turn which does not come would
        // never end. A test that awaits such a call fails for it, and is not held up by it.
        private Task<T> Made<T>(Task<T> call)
        {
            lock (_calls)
            {
                _calls.Add(call);
            }

            return call.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System);
        }
    }

    /// <summary>One call that reached Binance.</summary>
    /// <param name="At">What the clock showed.</param>
    /// <param name="Host">The host it was sent to.</param>
    /// <param name="Path">Its path.</param>
    /// <param name="ApiKey">The API key it carried, or null when it carried none.</param>
    private sealed record Arrival(DateTimeOffset At, string Host, string Path, string? ApiKey);
}
