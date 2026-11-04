using System.Diagnostics;
using System.Net;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Ai;

/// <summary>EXT-AI-03, FR-ROLE-06: retries and backoff come from each provider's settings.</summary>
public sealed class AiRetryTests(PostgresFixture postgres)
{
    private const string Complete = "/v1/complete";

    [Fact]
    public async Task A_call_that_succeeds_after_retries_counts_every_attempts_tokens()
    {
        var price = new AiModelPrice(3m, 15m);
        await using var ai = new AiHarness(postgres);
        ai.Provider.OnSequence(
            HttpMethod.Post,
            Complete,
            _ => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "overloaded", "Busy."),
            _ => FakeAiProvider.Completion("No.", 10, 2, stop: "refusal"),
            _ => FakeAiProvider.Completion("""{"verdict":"approve"}""", 100, 20, stop: "max_tokens"));

        var response = await ai.CompleteAsync(request: AiHarness.Request(price: price));

        Assert.Equal(
            ("""{"verdict":"approve"}""", 3, new AiUsage(110, 22), (decimal?)price.CostOf(new AiUsage(110, 22)), true),
            (response.Text, response.Attempts, response.Usage, response.Cost, response.Truncated));
    }

    [Fact]
    public async Task Without_a_price_a_call_has_no_cost()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Completion());

        var response = await ai.CompleteAsync();

        Assert.Null(response.Cost);
        Assert.Equal(1, response.Attempts);
    }

    [Fact]
    public async Task With_no_retries_configured_a_call_is_sent_once()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?> { ["AiProviders:Test:MaxRetries"] = "0" });
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Json("{}", HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.Overloaded, 1), (exception.Failure, exception.Attempts));
    }

    [Fact]
    public async Task The_providers_retry_after_is_waited_for()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.OnSequence(
            HttpMethod.Post,
            Complete,
            _ => FakeAiProvider.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", "Slow down.", ("Retry-After", "1")),
            _ => FakeAiProvider.Completion());
        var started = Stopwatch.GetTimestamp();

        var response = await ai.CompleteAsync();

        Assert.Equal(2, response.Attempts);
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(950));
    }

    /// <summary>A cycle has a deadline, and a fallback model is a better use of it (FR-ROLE-06).</summary>
    [Fact]
    public async Task A_retry_after_over_a_minute_isnt_waited_for()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", "Slow down.", ("Retry-After", "61")));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.RateLimit, 1, (TimeSpan?)TimeSpan.FromSeconds(61)), (exception.Failure, exception.Attempts, exception.RetryAfter));
    }

    /// <summary>Equal jitter: each wait is between half and all of the backoff, which doubles. 200 ms then 400 ms waits at least 300 ms.</summary>
    [Fact]
    public async Task Without_a_retry_after_the_backoff_doubles()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?> { ["AiProviders:Test:RetryBackoffMs"] = "200" });
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Json("{}", HttpStatusCode.InternalServerError));
        var started = Stopwatch.GetTimestamp();

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal(3, exception.Attempts);
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(290));
    }

    [Fact]
    public async Task Cancelling_a_call_in_flight_isnt_retried()
    {
        await using var ai = new AiHarness(postgres);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(AiHarness.Cancellation);
        ai.Provider.On(HttpMethod.Post, Complete, async (_, cancellationToken) =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return FakeAiProvider.Completion();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ai.CompleteAsync(cancellationToken: caller.Token));

        Assert.Single(ai.Provider.Requests);
    }

    [Fact]
    public async Task Cancelling_during_a_backoff_stops_the_wait()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?> { ["AiProviders:Test:RetryBackoffMs"] = "60000" });
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Json("{}", HttpStatusCode.InternalServerError));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(AiHarness.Cancellation);
        caller.CancelAfter(TimeSpan.FromMilliseconds(500));
        var started = Stopwatch.GetTimestamp();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ai.CompleteAsync(cancellationToken: caller.Token));

        Assert.Single(ai.Provider.Requests);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
    }
}
