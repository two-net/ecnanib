using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Resilience;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Ai.Resilience;

public class AiRetryPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AiRetryPolicy Create(FakeTimeProvider time, double jitter = 1.0, int maxAttempts = 3) =>
        new(
            Options.Create(new AiOptions { Retry = new AiRetryOptions { MaxAttempts = maxAttempts, BaseDelayMs = 500, MaxDelayMs = 10000, MaxRetryAfterWaitSeconds = 30 } }),
            time,
            NullLogger<AiRetryPolicy>.Instance,
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
                throw new AiUnavailableException("boom", AiProvider.Gpt);
            return Task.FromResult(42);
        }, "op", Ct);

        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromMilliseconds(499));
        await AiTestHost.WaitForAsync(() => calls == 1);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await AiTestHost.WaitForAsync(() => calls == 2);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        await AiTestHost.WaitForAsync(() => calls == 3);

        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time, maxAttempts: 2);
        var calls = 0;

        var task = policy.ExecuteAsync<int>(_ => { calls++; throw new AiUnavailableException("boom", AiProvider.Gpt, AiErrorKind.Timeout); }, "op", Ct);
        time.Advance(TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(AiErrorKind.Timeout, ex.Kind);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(AiErrorKind.InvalidRequest)]
    [InlineData(AiErrorKind.QuotaExceeded)]
    [InlineData(AiErrorKind.UnknownModel)]
    [InlineData(AiErrorKind.InvalidResponse)]
    public async Task DoesNotRetryNonTransientErrors(AiErrorKind kind)
    {
        var policy = Create(new FakeTimeProvider());
        var calls = 0;

        await Assert.ThrowsAsync<AiRequestException>(() =>
            policy.ExecuteAsync<int>(_ => { calls++; throw new AiRequestException("bad", AiProvider.Gpt, kind); }, "op", Ct));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AuthFailuresAreNeverRetried()
    {
        var policy = Create(new FakeTimeProvider());
        var calls = 0;

        await Assert.ThrowsAsync<AiAuthException>(() =>
            policy.ExecuteAsync<int>(_ => { calls++; throw new AiAuthException("bad key", AiProvider.Claude); }, "op", Ct));

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
                throw new AiRateLimitedException("429", AiProvider.Gpt, TimeSpan.FromSeconds(5), null);
            return Task.FromResult("ok");
        }, "op", Ct);

        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(20, Ct);
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await AiTestHost.WaitForAsync(() => calls == 2);
        Assert.Equal("ok", await task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task HonoursRetryAfterOnOverloadToo()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time);
        var calls = 0;

        var task = policy.ExecuteAsync(_ =>
        {
            calls++;
            if (calls == 1)
                throw new AiUnavailableException("529", AiProvider.Claude, AiErrorKind.Overloaded, retryAfter: TimeSpan.FromSeconds(5));
            return Task.FromResult("ok");
        }, "op", Ct);

        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(20, Ct);
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await AiTestHost.WaitForAsync(() => calls == 2);
        Assert.Equal("ok", await task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task RethrowsImmediatelyWhenRetryAfterExceedsMaximumWait()
    {
        var policy = Create(new FakeTimeProvider());
        var calls = 0;

        var ex = await Assert.ThrowsAsync<AiRateLimitedException>(() =>
            policy.ExecuteAsync<int>(_ => { calls++; throw new AiRateLimitedException("429", AiProvider.Gemini, TimeSpan.FromMinutes(2), null); }, "op", Ct));

        Assert.Equal(TimeSpan.FromMinutes(2), ex.RetryAfter);
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
                throw new AiUnavailableException("boom", AiProvider.Grok);
            return Task.FromResult(1);
        }, "op", Ct);

        // attempt 1 -> 625 ms, 2 -> 1250, 3 -> 2500, 4 -> 5000, 5 -> 10000, 6..7 -> 10000 (cap) * 1.25 = 12500
        foreach (var ms in new[] { 625, 1250, 2500, 5000, 10000, 12500, 12500 })
        {
            var before = calls;
            time.Advance(TimeSpan.FromMilliseconds(ms - 1));
            await Task.Delay(5, Ct);
            Assert.Equal(before, calls);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await AiTestHost.WaitForAsync(() => calls == before + 1);
        }

        Assert.Equal(1, await task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task CancellationStopsWaiting()
    {
        var time = new FakeTimeProvider();
        var policy = Create(time);
        using var cts = new CancellationTokenSource();

#pragma warning disable xUnit1051 // the test cancels this token itself
        var task = policy.ExecuteAsync<int>(_ => throw new AiUnavailableException("boom", AiProvider.Gpt), "op", cts.Token);
#pragma warning restore xUnit1051
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }
}
