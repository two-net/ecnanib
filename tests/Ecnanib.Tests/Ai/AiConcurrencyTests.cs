using System.Net;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Ai;

/// <summary>EXT-AI-03, NFR-REL-05: concurrent calls are limited per credential, and one credential never waits on another.</summary>
public sealed class AiConcurrencyTests(PostgresFixture postgres)
{
    private const string Complete = "/v1/complete";

    /// <summary>Long enough that a third call let through by mistake would have reached the fake.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private static CancellationToken Cancellation => AiHarness.Cancellation;

    [Fact]
    public async Task At_most_the_configured_number_of_calls_run_at_once_on_a_credential()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?> { ["AiProviders:Test:MaxConcurrentPerCredential"] = "2" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Provider.On(HttpMethod.Post, Complete, async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);

            return FakeAiProvider.Completion();
        });
        var busy = Guid.NewGuid();

        var calls = Enumerable.Range(0, 3).Select(_ => ai.CompleteAsync(AiHarness.Key(apiKey: "busy", credentialId: busy))).ToList();
        await LoggedApp.UntilAsync(() => ValueTask.FromResult(ai.Provider.InFlight("busy") == 2));

        // Another credential goes straight through while the first is full.
        var other = ai.CompleteAsync(AiHarness.Key(apiKey: "other"));
        await LoggedApp.UntilAsync(() => ValueTask.FromResult(ai.Provider.InFlight("other") == 1));
        await Task.Delay(Settle, Cancellation);
        Assert.Equal(2, ai.Provider.WithKey("busy").Count);

        release.SetResult();
        await Task.WhenAll([.. calls, other]);

        Assert.Equal((3, 2), (ai.Provider.WithKey("busy").Count, ai.Provider.MaxInFlight("busy")));
    }

    [Fact]
    public async Task A_failed_call_gives_its_slot_back()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?>
        {
            ["AiProviders:Test:MaxConcurrentPerCredential"] = "1",
            ["AiProviders:Test:MaxRetries"] = "0",
        });
        ai.Provider.OnSequence(HttpMethod.Post, Complete, _ => FakeAiProvider.Json("{}", HttpStatusCode.InternalServerError), _ => FakeAiProvider.Completion());
        var key = AiHarness.Key();

        await Assert.ThrowsAsync<Modules.AiAdapters.Contracts.AiException>(() => ai.CompleteAsync(key));
        var response = await ai.CompleteAsync(key).WaitAsync(TimeSpan.FromSeconds(10), Cancellation);

        Assert.Equal(1, response.Attempts);
    }

    [Fact]
    public async Task A_cancelled_call_gives_its_slot_back()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?> { ["AiProviders:Test:MaxConcurrentPerCredential"] = "1" });
        var calls = 0;
        ai.Provider.On(HttpMethod.Post, Complete, async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return FakeAiProvider.Completion();
        });
        var key = AiHarness.Key();
        using var first = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var hanging = ai.CompleteAsync(key, cancellationToken: first.Token);
        await LoggedApp.UntilAsync(() => ValueTask.FromResult(ai.Provider.InFlight(AiHarness.ApiKey) == 1));
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging);

        var response = await ai.CompleteAsync(key).WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        Assert.Equal(1, response.Attempts);
    }

    [Fact]
    public async Task Waiting_for_a_slot_can_be_cancelled_and_sends_nothing()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?> { ["AiProviders:Test:MaxConcurrentPerCredential"] = "1" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Provider.On(HttpMethod.Post, Complete, async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);

            return FakeAiProvider.Completion();
        });
        var key = AiHarness.Key();
        var holding = ai.CompleteAsync(key);
        await LoggedApp.UntilAsync(() => ValueTask.FromResult(ai.Provider.InFlight(AiHarness.ApiKey) == 1));
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        waiting.CancelAfter(Settle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ai.CompleteAsync(key, cancellationToken: waiting.Token));

        release.SetResult();
        await holding;
        Assert.Single(ai.Provider.Requests);
    }
}
