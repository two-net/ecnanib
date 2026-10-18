using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Exchange.Resilience;

public class ExchangeRetryPolicyTests
{
    private static ExchangeRetryPolicy Create(FakeTimeProvider time, double jitter = 1.0, int maxAttempts = 3) =>
        new(
            Options.Create(new ExchangeOptions { Retry = new ExchangeRetryOptions { MaxAttempts = maxAttempts, BaseDelayMs = 500, MaxDelayMs = 10000, MaxRetryAfterWaitSeconds = 30 } }),
            time,
            NullLogger<ExchangeRetryPolicy>.Instance,
            () => jitter);

    [Fact]
    public async Task RetriesTransientFailuresWithExponentialDelays()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time);
        var calls = 0;

        var task = policy.ExecuteAsync(_ =>
        {
            calls++;
            if (calls < 3)
                throw new ExchangeUnavailableException("boom");
            return Task.FromResult(42);
        }, RetryProfile.Query, "op", TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromMilliseconds(499));
        await WaitFor(() => calls == 1);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitFor(() => calls == 2);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        await WaitFor(() => calls == 3);

        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time, maxAttempts: 2);
        var calls = 0;

        var task = policy.ExecuteAsync<int>(_ => { calls++; throw new ExchangeUnavailableException("boom"); }, RetryProfile.Query, "op", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ExchangeUnavailableException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DoesNotRetryNonTransientErrors()
    {
        var policy = Create(new FakeTimeProvider());
        var calls = 0;

        await Assert.ThrowsAsync<ExchangeRequestException>(() =>
            policy.ExecuteAsync<int>(_ => { calls++; throw new ExchangeRequestException("bad", ExchangeErrorKind.InvalidPrice); }, RetryProfile.Query, "op", TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NoneProfileNeverRetries()
    {
        var policy = Create(new FakeTimeProvider());
        var calls = 0;

        await Assert.ThrowsAsync<ExchangeUnavailableException>(() =>
            policy.ExecuteAsync<int>(_ => { calls++; throw new ExchangeUnavailableException("boom"); }, RetryProfile.None, "place", TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HonoursRetryAfterWhenLongerThanBackoff()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time);
        var calls = 0;

        var task = policy.ExecuteAsync(_ =>
        {
            calls++;
            if (calls == 1)
                throw new ExchangeRateLimitedException("429", TimeSpan.FromSeconds(5), null, false);
            return Task.FromResult("ok");
        }, RetryProfile.Query, "op", TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => calls == 2);
        Assert.Equal("ok", await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RethrowsImmediatelyWhenRetryAfterExceedsMaximumWait()
    {
        var policy = Create(new FakeTimeProvider());
        var calls = 0;

        var ex = await Assert.ThrowsAsync<ExchangeRateLimitedException>(() =>
            policy.ExecuteAsync<int>(_ => { calls++; throw new ExchangeRateLimitedException("418", TimeSpan.FromMinutes(2), null, true); }, RetryProfile.Query, "op", TestContext.Current.CancellationToken));

        Assert.True(ex.IsIpBan);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DelayIsJitteredAndCapped()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time, jitter: 1.25, maxAttempts: 10);
        var calls = 0;
        var task = policy.ExecuteAsync(_ =>
        {
            calls++;
            if (calls < 8)
                throw new ExchangeUnavailableException("boom");
            return Task.FromResult(1);
        }, RetryProfile.Query, "op", TestContext.Current.CancellationToken);

        // attempt 1 -> 625 ms, 2 -> 1250, 3 -> 2500, 4 -> 5000, 5 -> 10000, 6..7 -> 10000 (cap) * 1.25 = 12500
        foreach (var ms in new[] { 625, 1250, 2500, 5000, 10000, 12500, 12500 })
        {
            var before = calls;
            time.Advance(TimeSpan.FromMilliseconds(ms - 1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
            Assert.Equal(before, calls);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitFor(() => calls == before + 1);
        }

        Assert.Equal(1, await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationStopsWaiting()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time);
        using var cts = new CancellationTokenSource();

#pragma warning disable xUnit1051 // the test cancels this token itself
        var task = policy.ExecuteAsync<int>(_ => throw new ExchangeUnavailableException("boom"), RetryProfile.Query, "op", cts.Token);
#pragma warning restore xUnit1051
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met.");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }
}
