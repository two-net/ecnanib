using System.Diagnostics;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// How long a call to a model may take comes from the configuration file, for each provider:
/// <c>AiProviders:&lt;Provider&gt;:TimeoutSeconds</c>. It is the only limit on the call, and a call
/// that runs into it ends as a timeout, which its caller may answer by calling again (FR-TEAM-9).
/// </summary>
public class AiCallTimeoutTests
{
    [Theory]

    // The file of the tests gives each provider a limit of its own.
    [InlineData("claude", 30)]
    [InlineData("gemini", 31)]
    [InlineData("gpt", 32)]
    [InlineData("grok", 33)]
    public async Task A_call_is_given_up_after_the_time_that_the_file_gives_its_provider(string code, int seconds)
    {
        // A provider that never answers, and a clock that shows which limit the call was given.
        var clock = new TimerClock();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StandInSystem(async (_, givenUp) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, givenUp);

            throw new UnreachableException();
        });
        using var application = provider.Behind(
            AiProviderGatewayModule.HttpClientName, services: services => services.AddSingleton<TimeProvider>(clock));
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var call = gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.RequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The limit is this provider's, and not that of another: nothing was set for theirs.
        var limit = TimeSpan.FromSeconds(seconds);
        Assert.Contains(limit, clock.Timers);
        Assert.All(
            new[] { 30, 31, 32, 33 }.Where(other => other != seconds),
            other => Assert.DoesNotContain(TimeSpan.FromSeconds(other), clock.Timers));
        Assert.False(call.IsCompleted);

        // When it runs out, the call ends: as an outcome, and not as an error.
        clock.RunOut(limit);
        var result = await call;

        Assert.Equal(new AiCallResult(AiCallOutcome.TimedOut, null, null, result.Latency), result);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public void The_client_of_the_gateway_has_no_limit_of_its_own()
    {
        var provider = ModelCalls.ThatAnswer();
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);

        // A client of the factory gives up after 100 seconds unless told otherwise, and the file
        // may give a provider up to 600.
        using var client = application.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AiProviderGatewayModule.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
